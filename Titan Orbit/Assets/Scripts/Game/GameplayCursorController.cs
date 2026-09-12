using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only combat mouse pointer. Hides the OS arrow while flying and draws a
    /// screen-space reticle that follows the mouse: general crosshair, MEGA reticle, or
    /// MEGA+Shift manual-aim. Menus, death, match-end, and touch UI restore the OS arrow.
    /// <para>
    /// [TITAN-ORBIT] We do <em>not</em> use <c>Cursor.SetCursor</c> for the combat look.
    /// Windows hardware cursors are capped at 32×32 (thin line art vanishes) and the Editor
    /// Game view often keeps the OS arrow anyway. A UI Image is the pointer the player
    /// actually sees. Aim still uses the OS mouse position via <see cref="PlayerInputHandler"/>.
    /// </para>
    /// Presentation only — no ECS writes, no ghosts, no RPCs. Lives on NceGameRoot.
    /// Dedicated-server processes disable immediately.
    /// Paired with <see cref="EcsGameBridge.TryGetLocalMegaShipState"/> and
    /// <see cref="PlayerInputHandler.OverdriveHeld"/>.
    /// </summary>
    [DefaultExecutionOrder(67000)]
    public class GameplayCursorController : MonoBehaviour
    {
        /// <summary>
        /// Which pointer is showing. Sprite / OS-visibility swaps only when this changes.
        /// Position still updates every LateUpdate while a combat reticle is up.
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

        /// <summary>On-screen reticle size in overlay pixels. Large enough that thin line art stays readable.</summary>
        const float CursorSizePixels = 80f;

        /// <summary>Ice-cyan tint so white pack icons read as HUD chrome on the dark map.</summary>
        static readonly Color ReticleColor = new Color(0.45f, 0.92f, 1f, 1f);

        /// <summary>Near-black duplicate behind the reticle so it stays visible over bright planets.</summary>
        static readonly Color ShadowColor = new Color(0.02f, 0.04f, 0.08f, 0.9f);

        [Header("Cursor textures")]
        /// <summary>
        /// Simple line crosshair for a normal family hull. Read/Write should stay on so
        /// we can <see cref="Sprite.Create"/> a runtime sprite for the HUD Image.
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

        /// <summary>Overlay canvas that owns the reticle. No GraphicRaycaster — it must not steal HUD clicks.</summary>
        Canvas _cursorCanvas;

        /// <summary>Root rect we park on the mouse pixel (pivot centre = aim hotspot).</summary>
        RectTransform _cursorRoot;

        /// <summary>Dark copy of the reticle, slightly larger, for contrast.</summary>
        Image _shadowImage;

        /// <summary>Tinted combat reticle the player sees.</summary>
        Image _reticleImage;

        Sprite _generalSprite;
        Sprite _megaSprite;
        Sprite _megaManualSprite;

        /// <summary>
        /// [UNITY] Domain Reload off leaves static overlay flags hot. Clear them so a leftover
        /// death / match-end / orbit-menu flag from the last Play Mode session cannot pin the OS arrow.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad()
        {
            DeathScreenController.ClearShowingFlag();
            MatchEndScreenController.ClearShowingFlag();
            MoonOrbitClientState.SetOrbitMenuVisible(false);
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
            BuildSprites();
            EnsureCursorUi();
            HideReticle();
        }

        /// <summary>
        /// After every gameplay Update (input + overlay show/hide). Resolves the mode,
        /// swaps the reticle when it changes, and follows the mouse while flying.
        /// </summary>
        void LateUpdate()
        {
            GameplayCursorMode next = ResolveMode();
            if (next != _appliedMode)
            {
                ApplyMode(next);
                _appliedMode = next;
            }

            // --- Follow the mouse ---
            // Screen Space Overlay world position is the pixel. Pivot is centred so the
            // reticle sits on the same point PlayerInputHandler unprojects for aim.
            if (next != GameplayCursorMode.SystemDefault && TryReadMouseScreen(out Vector2 screen))
                _cursorRoot.position = screen;
        }

        /// <summary>
        /// [UNITY] Component disabled (leave Play Mode, destroy root). Restore the OS arrow
        /// so the Editor and Main Menu do not keep a leftover combat reticle.
        /// </summary>
        void OnDisable()
        {
            RestoreSystemCursor();
            HideReticle();
            _appliedMode = (GameplayCursorMode)(-1);
        }

        /// <summary>[UNITY] Drops runtime sprites we created so Domain Reload does not leak them.</summary>
        void OnDestroy()
        {
            DestroySprite(ref _generalSprite);
            DestroySprite(ref _megaSprite);
            DestroySprite(ref _megaManualSprite);
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
            // --- In-match command / store / plaques ---
            // These can sit on top of a live ship, so they always win.
            if (InGameEscapeMenuController.IsOpen)
                return true;
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return true;
            if (ShipCommsClientState.IsOpen)
                return true;
            if (DeathScreenController.IsShowing)
                return true;
            if (MatchEndScreenController.IsShowing)
                return true;

            // --- Title / join ---
            // Ignore leftover Main Menu / Join Game flags once we already have a live hull
            // in a match. Those overlays should be off; a stuck activeInHierarchy must not
            // pin the OS arrow for the whole flight.
            bool inMatchWithShip = EcsGameBridge.IsNetworkInGame() &&
                                   (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose);
            if (inMatchWithShip)
                return false;

            if (_flow != null && _flow.IsMainMenuVisible)
                return true;
            if (_joinBrowser != null && _joinBrowser.IsVisible)
                return true;

            return false;
        }

        /// <summary>
        /// Shows or hides the HUD reticle and toggles the OS arrow. Called only on mode change.
        /// </summary>
        /// <param name="mode">Mode resolved this frame.</param>
        void ApplyMode(GameplayCursorMode mode)
        {
            if (mode == GameplayCursorMode.SystemDefault)
            {
                RestoreSystemCursor();
                HideReticle();
                return;
            }

            Sprite sprite = SpriteForMode(mode);
            if (sprite == null)
            {
                RestoreSystemCursor();
                HideReticle();
                return;
            }

            // --- Combat pointer ---
            // Hide the OS arrow so the player sees one reticle, not two stacked pointers.
            Cursor.visible = false;
            Cursor.SetCursor(null, Vector2.zero, UnityEngine.CursorMode.Auto);

            if (_shadowImage != null)
                _shadowImage.sprite = sprite;
            if (_reticleImage != null)
                _reticleImage.sprite = sprite;
            if (_cursorRoot != null)
                _cursorRoot.gameObject.SetActive(true);
        }

        /// <summary>Runtime sprite for a combat mode. Null if that texture never loaded.</summary>
        Sprite SpriteForMode(GameplayCursorMode mode)
        {
            switch (mode)
            {
                case GameplayCursorMode.Mega:
                    return _megaSprite;
                case GameplayCursorMode.MegaManual:
                    return _megaManualSprite;
                default:
                    return _generalSprite;
            }
        }

        /// <summary>Hands the OS pointer back and makes it visible again.</summary>
        static void RestoreSystemCursor()
        {
            Cursor.visible = true;
            Cursor.SetCursor(null, Vector2.zero, UnityEngine.CursorMode.Auto);
        }

        /// <summary>Turns the HUD reticle off without touching the OS cursor.</summary>
        void HideReticle()
        {
            if (_cursorRoot != null)
                _cursorRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Reads the Input System mouse in Game-view pixels. False on MPPM unfocused views
        /// that report NaN (same guard as <see cref="PlayerInputHandler"/>).
        /// </summary>
        static bool TryReadMouseScreen(out Vector2 screenPos)
        {
            screenPos = default;
            if (Mouse.current == null)
                return false;

            Vector2 raw = Mouse.current.position.ReadValue();
            if (!float.IsFinite(raw.x) || !float.IsFinite(raw.y))
                return false;

            screenPos = raw;
            return true;
        }

        /// <summary>
        /// Builds a Screen Space Overlay canvas with a centred shadow + reticle Image.
        /// No raycaster — clicks still hit the real HUD / world.
        /// </summary>
        void EnsureCursorUi()
        {
            if (_cursorRoot != null)
                return;

            var canvasGo = new GameObject("GameplayCursorCanvas");
            canvasGo.transform.SetParent(transform, false);

            _cursorCanvas = canvasGo.AddComponent<Canvas>();
            _cursorCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above match-end (9000) and other HUD so the reticle is never under a plaque.
            _cursorCanvas.sortingOrder = 32000;
            _cursorCanvas.pixelPerfect = true;

            var rootGo = new GameObject("Reticle");
            rootGo.transform.SetParent(canvasGo.transform, false);
            _cursorRoot = rootGo.AddComponent<RectTransform>();
            _cursorRoot.pivot = new Vector2(0.5f, 0.5f);
            _cursorRoot.anchorMin = new Vector2(0f, 0f);
            _cursorRoot.anchorMax = new Vector2(0f, 0f);
            _cursorRoot.sizeDelta = new Vector2(CursorSizePixels, CursorSizePixels);

            _shadowImage = CreateLayer(rootGo.transform, "Shadow", CursorSizePixels + 4f, ShadowColor);
            _reticleImage = CreateLayer(rootGo.transform, "Icon", CursorSizePixels, ReticleColor);
        }

        /// <summary>
        /// Adds a full-stretch Image under <paramref name="parent"/>.
        /// <c>raycastTarget</c> stays false so the pointer never blocks HUD clicks.
        /// </summary>
        static Image CreateLayer(Transform parent, string name, float size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);

            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            image.color = color;
            return image;
        }

        /// <summary>One-shot <see cref="Sprite.Create"/> for each loaded texture.</summary>
        void BuildSprites()
        {
            _generalSprite = CreateCenteredSprite(generalCursor);
            _megaSprite = CreateCenteredSprite(megaCursor);
            _megaManualSprite = CreateCenteredSprite(megaManualCursor);
        }

        /// <summary>
        /// Turns a Cursor-type texture into a centred sprite. Returns null when the
        /// texture is missing or has no pixels (SetCursor would also fail in that case).
        /// </summary>
        static Sprite CreateCenteredSprite(Texture2D tex)
        {
            if (tex == null || tex.width < 2 || tex.height < 2)
                return null;

            return Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);
        }

        /// <summary>Destroys a runtime sprite we created. Scene-imported sprites are left alone.</summary>
        static void DestroySprite(ref Sprite sprite)
        {
            if (sprite == null)
                return;
            Destroy(sprite);
            sprite = null;
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
