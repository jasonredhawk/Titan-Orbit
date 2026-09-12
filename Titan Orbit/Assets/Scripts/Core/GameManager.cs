using System;
using TitanOrbit;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Editor multiplayer workflow for this machine: Test (local Client &amp; Server) vs
    /// Production (Client-only join to a dedicated server via UGS/Relay).
    /// Stored on <see cref="GameManager"/> so you can flip it in the Inspector instead of the
    /// Titan Orbit menu. Applied only by the Editor custom inspector — has no runtime effect in builds.
    /// </summary>
    public enum EditorMultiplayerMode
    {
        /// <summary>Local play: NetCode PlayMode Type = Client &amp; Server; Local play menu buttons shown.</summary>
        Test = 0,

        /// <summary>Dedicated join: NetCode PlayMode Type = Client; Local play buttons hidden (production-style menu).</summary>
        Production = 1
    }

    /// <summary>
    /// Scene singleton that holds designer-tunable HUD options, background presentation toggles,
    /// debug flags for local play, and the Editor Test / Production multiplayer toggle. Lives on
    /// <c>NceGameRoot</c> in SampleScene (Inspector → Game Manager). Moon orbit UI reads
    /// <see cref="DebugFreeShipUpgradeTree"/>, <see cref="DebugFreeGear"/>, and
    /// <see cref="DebugFreeCards"/> so you can click any upgrade-tree node, buy GEAR, or spin
    /// CARDS for free during testing. Also gates optional tools such as Instruction Image Capture
    /// (F8/F9 reference plates) and the stutter isolator. Background checkboxes independently
    /// enable the nebula quad and the shader starfield (both can be on at once). Publishes debug
    /// values to <see cref="TitanOrbitDebugFlags"/> so other assemblies can honor toggles without
    /// referencing this Core assembly. Dedicated server builds normally leave debug flags false.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        /// <summary>Global access for UI and tools that need debug flags without scene references.</summary>
        public static GameManager Instance { get; private set; }

        /// <summary>
        /// Play Mode only: fired when <see cref="ShowSpeedometer"/> is published so
        /// <c>ShipSpeedometerHUD</c> can disable its own component (no LateUpdate) when off.
        /// </summary>
        public static event Action<bool> ShowSpeedometerChanged;

        /// <summary>
        /// Play Mode only: fired when <see cref="ShowSpaceBackground"/> is published so
        /// <c>ScrollingSpaceBackground</c> can hide its nebula quad and skip LateUpdate when off.
        /// </summary>
        public static event Action<bool> ShowSpaceBackgroundChanged;

        /// <summary>
        /// Play Mode only: fired when <see cref="ShowStarfieldBackground"/> is published so
        /// <c>ParallaxStarfieldBackground</c> can hide its star quad and skip LateUpdate when off.
        /// </summary>
        public static event Action<bool> ShowStarfieldBackgroundChanged;

        // [EDITOR] / [TITAN-ORBIT] Inspector Test|Production toolbar (see GameManagerEditor) applies
        // the same NetCode prefs as Titan Orbit > Configure Multiplayer For Local Play / Dedicated Server.
        // This serialized value is a reminder of what you last chose; the custom inspector syncs it
        // from live PlayMode Tools prefs when you select the component.
        [Header("Multiplayer Mode (Editor)")]
        [Tooltip("Test = local Client & Server + Local play UI. Production = Client-only + UGS/Relay join (hides Local play). Same as Titan Orbit > Configure Multiplayer menus. Editor-only — does not change player builds by itself (Production still writes TitanOrbitMultiplayerConfig for the next WebGL build).")]
        [SerializeField] EditorMultiplayerMode editorMultiplayerMode = EditorMultiplayerMode.Test;

        // [UNITY] / [TITAN-ORBIT] Production may hide the telemetry panel; Test often keeps it on.
        // When false, ShipSpeedometerHUD disables itself — no LateUpdate, no ECS queries.
        [Header("HUD")]
        [Tooltip("Local-player speedometer (speed / accel / mass / ram / bullets). Off = not drawn and the HUD component disables itself (no per-frame update). Useful to disable for Production polish while keeping it for Test.")]
        [SerializeField] bool showSpeedometer = true;

        /// <summary>Last value pushed to <see cref="ShowSpeedometerChanged"/> (avoids spam while editing).</summary>
        bool _hasPublishedShowSpeedometer;

        /// <summary>Mirror of the last published showSpeedometer for change detection.</summary>
        bool _lastPublishedShowSpeedometer;

        // [UNITY] / [TITAN-ORBIT] Independent presentation toggles. Both default on so the nebula
        // and the shader starfield can composite together; flip either off without touching the other.
        [Header("Background")]
        [Tooltip("Old space background: the scrolling nebula quad (ScrollingSpaceBackground). Off hides the quad and skips its LateUpdate. Can stay on together with the starfield.")]
        [SerializeField] bool showSpaceBackground = true;

        [Tooltip("New parallax starfield: shader-drawn star layers that shift as you fly. Off hides the star quad and skips its LateUpdate. Can stay on together with the nebula.")]
        [SerializeField] bool showStarfieldBackground = true;

        /// <summary>Last value pushed to <see cref="ShowSpaceBackgroundChanged"/> (avoids spam while editing).</summary>
        bool _hasPublishedShowSpaceBackground;

        /// <summary>Mirror of the last published showSpaceBackground for change detection.</summary>
        bool _lastPublishedShowSpaceBackground;

        /// <summary>Last value pushed to <see cref="ShowStarfieldBackgroundChanged"/> (avoids spam while editing).</summary>
        bool _hasPublishedShowStarfieldBackground;

        /// <summary>Mirror of the last published showStarfieldBackground for change detection.</summary>
        bool _lastPublishedShowStarfieldBackground;

        // [UNITY] Inspector toggle — when true, ship upgrade tree treats all nodes as free / clickable.
        [Header("Debug — Ship Upgrade Tree")]
        [Tooltip("When enabled, the moon orbit ship upgrade tree unlocks every node. Click any ship to try it for free (local Editor / development only).")]
        [SerializeField] bool debugFreeShipUpgradeTree;

        // [UNITY] Inspector toggle — when true, GEAR tab buys skip the gem debit.
        [Header("Debug — Gear")]
        [Tooltip("When enabled, the moon orbit GEAR tab buys drones, rockets, mines, and extra components without spending contributed gems. Loadout slot limits still apply (local Editor / development only).")]
        [SerializeField] bool debugFreeGear;

        // [UNITY] Inspector toggle — when true, CARDS tab spins skip the gem debit.
        [Header("Debug — Cards")]
        [Tooltip("When enabled, the moon orbit CARDS tab spins without spending contributed gems. You still need an empty loadout slot, then Choose one offer (local Editor / development only).")]
        [SerializeField] bool debugFreeCards;

        [Header("Debug — Bullet Banks")]
        [Tooltip("ON (Test): B-key and the bullet-type HUD walk every BulletVfxBank category, even if that family's weapon is not in the loadout (includes healing EnergySpheres). OFF (Production): only the hull default plus purchased weapon components in the loadout. Dedicated server always stays Production.")]
        [SerializeField] bool debugCycleAllBulletBanks;

        [Header("Debug — Thruster VFX")]
        [Tooltip("ON (Test): T-key walks every family in Resources/ThrusterVfxBank and rebuilds live ship jets so you can compare flames. OFF (Production): purchased / host-family jets only. Dedicated server always stays Production.")]
        [SerializeField] bool debugCycleAllThrusterVfx;

        [Header("Debug — Rockets")]
        [Tooltip("When enabled, ALT fires a homing rocket without consuming charges (and with an empty loadout). The 5s reload still applies. Local Editor / MPPM host only.")]
        [SerializeField] bool debugInfiniteRockets;

        [Header("Debug — Mines")]
        [Tooltip("When enabled, ALT places the focused mine pack without consuming charges (and with an empty loadout). The deploy cooldown still applies. Local Editor / MPPM host only.")]
        [SerializeField] bool debugInfiniteMines;

        [Header("Debug — Rocket / Mine Self-Harm")]
        [Tooltip("After 2 seconds, your own rockets and mines treat you (and your team) as an enemy so you can test hits and blasts on yourself. Local Editor / MPPM host only.")]
        [SerializeField] bool debugSelfHarmRocketsAndMines;

        [Header("Debug — Titan Ships")]
        [Tooltip("When enabled, Titan guns can auto-aim asteroids in damage mode — lowest priority after ships, planetary defense turrets, and moon shields. Heal mode is unchanged. Local Editor / MPPM host only.")]
        [SerializeField] bool debugMegaShipsAutoFireAsteroids;

        [Tooltip("Temporary isolate: skip MegaShipAutoFireSystem (no Titan auto-aim / turret slew). Leave OFF for normal play. Shift+Fire still aims at the mouse in BulletSimulationSystem. Honored on dedicated after rebuild.")]
        [SerializeField] bool debugDisableMegaShipAutoFire;

        [Header("Debug — Asteroid Destroy Hitch")]
        [Tooltip("Logs [AsteroidDestroy] timings in the Console when an asteroid explodes (local gem Instantiates + urgent gem proxies). Filter the Console with that tag.")]
        [SerializeField] bool debugLogAsteroidDestroyPerf;

        [Header("Debug — Instruction Image Capture")]
        [Tooltip("When enabled in Play Mode, shows the Instruction capture status banner and accepts F8/F9 after Join Team to gather reference plates for Resources/InstructionScreens art. Leave OFF for normal play (hides UI and ignores capture keys).")]
        [SerializeField] bool debugEnableInstructionImageCapture;

        [Header("Debug — Stutter Isolator")]
        [Tooltip("When enabled in Play Mode, shows an on-screen panel and accepts Shift+F1–F5 to temporarily disable impact VFX, floats, asteroid toroidal collision, ship soft-track, or gem burst. Leave OFF for normal play.")]
        [SerializeField] bool debugEnableStutterIsolator;

        [Tooltip("Starting value when the isolator is enabled: skip impact VFX (Shift+F1).")]
        [SerializeField] bool isolatorStartDisableImpactVfx;

        [Tooltip("Starting value when the isolator is enabled: skip floating damage/HP text (Shift+F2).")]
        [SerializeField] bool isolatorStartDisableFloatingCounts;

        [Tooltip("Starting value when the isolator is enabled: skip asteroid toroidal ship collision (Shift+F3).")]
        [SerializeField] bool isolatorStartDisableAsteroidShipCollision;

        [Tooltip("Starting value when the isolator is enabled: raw ship pose, no soft-track (Shift+F4).")]
        [SerializeField] bool isolatorStartDisableShipSoftTrack;

        [Tooltip("Starting value when the isolator is enabled: skip local gem burst (Shift+F5).")]
        [SerializeField] bool isolatorStartDisableGemBurst;

        /// <summary>True when the local-player speedometer HUD should run (Inspector on NceGameRoot).</summary>
        public bool ShowSpeedometer => showSpeedometer;

        /// <summary>True when the scrolling nebula space background should draw (Inspector on NceGameRoot).</summary>
        public bool ShowSpaceBackground => showSpaceBackground;

        /// <summary>True when the shader parallax starfield should draw (Inspector on NceGameRoot).</summary>
        public bool ShowStarfieldBackground => showStarfieldBackground;

        /// <summary>True when designers enabled free upgrades in the Inspector (client + local-host convenience).</summary>
        public bool DebugFreeShipUpgradeTree => debugFreeShipUpgradeTree;

        /// <summary>True when designers enabled free GEAR buys in the Inspector (client + local-host convenience).</summary>
        public bool DebugFreeGear => debugFreeGear;

        /// <summary>True when designers enabled free CARD spins in the Inspector (client + local-host convenience).</summary>
        public bool DebugFreeCards => debugFreeCards;

        /// <summary>
        /// True when B-key and the bullet-type HUD cycle every bank (Test).
        /// False is Production: purchased / loadout weapons only.
        /// </summary>
        public bool DebugCycleAllBulletBanks => debugCycleAllBulletBanks;

        /// <summary>
        /// True when T-key walks every <c>ThrusterVfxBank</c> family (Test).
        /// False is Production: purchased / host-family jets only.
        /// </summary>
        public bool DebugCycleAllThrusterVfx => debugCycleAllThrusterVfx;

        /// <summary>True when asteroid-destroy hitch logging is enabled in the Inspector.</summary>
        public bool DebugLogAsteroidDestroyPerf => debugLogAsteroidDestroyPerf;

        /// <summary>True when the InstructionScreens reference-capture tool (F8/F9) is enabled.</summary>
        public bool DebugEnableInstructionImageCapture => debugEnableInstructionImageCapture;

        /// <summary>True when the Shift+F stutter isolator overlay is enabled.</summary>
        public bool DebugEnableStutterIsolator => debugEnableStutterIsolator;

        /// <summary>
        /// Safe static check for the speedometer. Defaults <b>on</b> when no GameManager exists yet
        /// so early frames before Awake still match the previous always-on behavior.
        /// </summary>
        public static bool IsShowSpeedometerActive =>
            Instance == null || Instance.showSpeedometer;

        /// <summary>
        /// Safe static check for the nebula space background. Defaults <b>on</b> when no
        /// GameManager exists yet so early frames still match the previous always-on look.
        /// </summary>
        public static bool IsShowSpaceBackgroundActive =>
            Instance == null || Instance.showSpaceBackground;

        /// <summary>
        /// Safe static check for the shader starfield. Defaults <b>on</b> when no GameManager
        /// exists yet so a missing singleton does not hide the new background.
        /// </summary>
        public static bool IsShowStarfieldBackgroundActive =>
            Instance == null || Instance.showStarfieldBackground;

        /// <summary>
        /// Safe static check used by moon orbit UI. Also true when the Shared flag was published
        /// (covers the brief window before Instance is set, and keeps UI/server in sync).
        /// </summary>
        public static bool IsDebugFreeShipUpgradeTreeActive =>
            TitanOrbitDebugFlags.FreeShipUpgradeTree
            || (Instance != null && Instance.debugFreeShipUpgradeTree);

        /// <summary>
        /// Safe static check used by moon orbit GEAR UI. Also true when the Shared flag was published
        /// (covers the brief window before Instance is set, and keeps UI/server in sync).
        /// </summary>
        public static bool IsDebugFreeGearActive =>
            TitanOrbitDebugFlags.FreeGear
            || (Instance != null && Instance.debugFreeGear);

        /// <summary>
        /// Safe static check used by moon orbit CARDS UI. Also true when the Shared flag was published
        /// (covers the brief window before Instance is set, and keeps UI/server in sync).
        /// </summary>
        public static bool IsDebugFreeCardsActive =>
            TitanOrbitDebugFlags.FreeCards
            || (Instance != null && Instance.debugFreeCards);

        /// <summary>
        /// Ensures a GameManager exists for Play Mode. Prefer the component on NceGameRoot so you can
        /// toggle the flag in the Inspector. Never creates a second empty GameManager that would
        /// steal Instance and wipe the Inspector toggle.
        /// </summary>
        public static GameManager EnsureExists()
        {
            if (Instance != null)
            {
                Instance.PublishDebugFlags();
                return Instance;
            }

            var existing = UnityEngine.Object.FindFirstObjectByType<GameManager>();
            if (existing != null)
            {
                // Awake may not have run yet — adopt this scene object and publish its Inspector value.
                Instance = existing;
                existing.PublishDebugFlags();
                return existing;
            }

            var go = new GameObject("GameManager");
            return go.AddComponent<GameManager>();
        }

        /// <summary>
        /// [UNITY] Awake — enforces a single GameManager instance and publishes debug flags to Shared.
        /// </summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            // [STANDARD] Classic singleton guard — destroy late duplicates, keep the first.
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            PublishDebugFlags();
        }

        /// <summary>
        /// [UNITY] OnValidate — keeps Shared flags in sync when you flip the Inspector checkbox in Edit Mode
        /// (and again when entering Play Mode after a domain reload).
        /// </summary>
        void OnValidate()
        {
            // Only the live singleton (or this object before Awake) should publish.
            if (Instance != null && Instance != this)
                return;
            PublishDebugFlags();
        }

        /// <summary>
        /// [UNITY] OnDestroy — clears the static reference and Shared flags so a reloaded scene starts clean.
        /// </summary>
        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                TitanOrbitDebugFlags.FreeShipUpgradeTree = false;
                TitanOrbitDebugFlags.FreeGear = false;
                TitanOrbitDebugFlags.FreeCards = false;
                TitanOrbitDebugFlags.CycleAllBulletBanks = false;
                TitanOrbitDebugFlags.CycleAllThrusterVfx = false;
                TitanOrbitDebugFlags.InfiniteRockets = false;
                TitanOrbitDebugFlags.InfiniteMines = false;
                TitanOrbitDebugFlags.SelfHarmRocketsAndMines = false;
                TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids = false;
                TitanOrbitDebugFlags.DisableMegaShipAutoFire = false;
                TitanOrbitDebugFlags.LogAsteroidDestroyPerf = false;
                TitanOrbitDebugFlags.InstructionImageCaptureEnabled = false;
                TitanOrbitDebugFlags.StutterIsolatorEnabled = false;
                ClearIsolationFlags();
                _hasPublishedShowSpeedometer = false;
                _hasPublishedShowSpaceBackground = false;
                _hasPublishedShowStarfieldBackground = false;
            }
        }

        /// <summary>
        /// Copies Inspector fields into <see cref="TitanOrbitDebugFlags"/> for ECS / other assemblies,
        /// and notifies HUD / background listeners when presentation toggles change in Play Mode.
        /// </summary>
        public void PublishDebugFlags()
        {
            // [TITAN-ORBIT] ECS MoonOrbitStoreSystem cannot reference TitanOrbit.Core — Shared bridge.
            TitanOrbitDebugFlags.FreeShipUpgradeTree = debugFreeShipUpgradeTree;
            TitanOrbitDebugFlags.FreeGear = debugFreeGear;
            TitanOrbitDebugFlags.FreeCards = debugFreeCards;
            // [TITAN-ORBIT] Cycle-all / infinite ordnance are local Editor / MPPM conveniences.
            // Dedicated GCE stays Production so a baked-on Inspector tick cannot unlock
            // every bank or free rockets for remote clients.
#if UNITY_SERVER && !UNITY_EDITOR
            TitanOrbitDebugFlags.CycleAllBulletBanks = false;
            TitanOrbitDebugFlags.CycleAllThrusterVfx = false;
            TitanOrbitDebugFlags.InfiniteRockets = false;
            TitanOrbitDebugFlags.InfiniteMines = false;
            TitanOrbitDebugFlags.SelfHarmRocketsAndMines = false;
            TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids = false;
#else
            TitanOrbitDebugFlags.CycleAllBulletBanks = debugCycleAllBulletBanks;
            TitanOrbitDebugFlags.CycleAllThrusterVfx = debugCycleAllThrusterVfx;
            TitanOrbitDebugFlags.InfiniteRockets = debugInfiniteRockets;
            TitanOrbitDebugFlags.InfiniteMines = debugInfiniteMines;
            TitanOrbitDebugFlags.SelfHarmRocketsAndMines = debugSelfHarmRocketsAndMines;
            TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids = debugMegaShipsAutoFireAsteroids;
#endif
            // Isolate MEGA auto-aim in the Editor only. Dedicated Docker / Edgegap must
            // keep turret slew — a baked-on isolate left MEGA Phase B hull-forward with
            // client tracers at the mouse (no damage).
#if UNITY_SERVER && !UNITY_EDITOR
            TitanOrbitDebugFlags.DisableMegaShipAutoFire = false;
#else
            TitanOrbitDebugFlags.DisableMegaShipAutoFire = debugDisableMegaShipAutoFire;
#endif
            TitanOrbitDebugFlags.LogAsteroidDestroyPerf = debugLogAsteroidDestroyPerf;
            // [TITAN-ORBIT] Instruction capture stays OFF unless you flip this for art rebuilds —
            // otherwise F8/F9 and the bottom status banner stay inactive during normal play.
            TitanOrbitDebugFlags.InstructionImageCaptureEnabled = debugEnableInstructionImageCapture;
            TitanOrbitDebugFlags.StutterIsolatorEnabled = debugEnableStutterIsolator;

            // Seed isolation bits from Inspector when enabling; when master switch is OFF, clear them
            // so leftover Shift+F toggles from a previous Play session cannot stick.
            if (debugEnableStutterIsolator)
            {
                TitanOrbitDebugFlags.IsolateDisableImpactVfx = isolatorStartDisableImpactVfx;
                TitanOrbitDebugFlags.IsolateDisableFloatingCounts = isolatorStartDisableFloatingCounts;
                TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision =
                    isolatorStartDisableAsteroidShipCollision;
                TitanOrbitDebugFlags.IsolateDisableShipSoftTrack = isolatorStartDisableShipSoftTrack;
                TitanOrbitDebugFlags.IsolateDisableGemBurst = isolatorStartDisableGemBurst;
            }
            else
            {
                ClearIsolationFlags();
            }

            // --- HUD + backgrounds: presentation on/off ---
            NotifyShowSpeedometerChangedIfNeeded();
            NotifyShowSpaceBackgroundChangedIfNeeded();
            NotifyShowStarfieldBackgroundChangedIfNeeded();
        }

        /// <summary>
        /// Invokes <see cref="ShowSpeedometerChanged"/> in Play Mode when the value changes
        /// (or on the first publish after Awake so late subscribers can sync via Start instead).
        /// </summary>
        void NotifyShowSpeedometerChangedIfNeeded()
        {
            NotifyBoolChangedIfNeeded(
                ref _hasPublishedShowSpeedometer,
                ref _lastPublishedShowSpeedometer,
                showSpeedometer,
                ShowSpeedometerChanged);
        }

        /// <summary>
        /// Invokes <see cref="ShowSpaceBackgroundChanged"/> in Play Mode when the nebula toggle
        /// changes (or on the first publish after Awake).
        /// </summary>
        void NotifyShowSpaceBackgroundChangedIfNeeded()
        {
            NotifyBoolChangedIfNeeded(
                ref _hasPublishedShowSpaceBackground,
                ref _lastPublishedShowSpaceBackground,
                showSpaceBackground,
                ShowSpaceBackgroundChanged);
        }

        /// <summary>
        /// Invokes <see cref="ShowStarfieldBackgroundChanged"/> in Play Mode when the starfield
        /// toggle changes (or on the first publish after Awake).
        /// </summary>
        void NotifyShowStarfieldBackgroundChangedIfNeeded()
        {
            NotifyBoolChangedIfNeeded(
                ref _hasPublishedShowStarfieldBackground,
                ref _lastPublishedShowStarfieldBackground,
                showStarfieldBackground,
                ShowStarfieldBackgroundChanged);
        }

        /// <summary>
        /// Shared Play Mode publisher for Inspector bools that drive client presentation.
        /// Skips Edit Mode so OnValidate cannot poke live components. Fires on first publish
        /// and whenever <paramref name="current"/> differs from the last sent value.
        /// </summary>
        /// <param name="hasPublished">Per-flag latch so the first Play Mode publish always fires.</param>
        /// <param name="lastPublished">Last value sent to <paramref name="evt"/>.</param>
        /// <param name="current">Inspector field value right now.</param>
        /// <param name="evt">Static event listeners subscribe to (HUD, nebula, starfield).</param>
        static void NotifyBoolChangedIfNeeded(
            ref bool hasPublished,
            ref bool lastPublished,
            bool current,
            Action<bool> evt)
        {
            // [UNITY] Edit Mode OnValidate must not poke play-mode presentation components.
            if (!Application.isPlaying)
                return;

            if (hasPublished && lastPublished == current)
                return;

            hasPublished = true;
            lastPublished = current;
            evt?.Invoke(current);
        }

        /// <summary>Resets all Shift+F isolation bits to off (normal gameplay).</summary>
        static void ClearIsolationFlags()
        {
            TitanOrbitDebugFlags.IsolateDisableImpactVfx = false;
            TitanOrbitDebugFlags.IsolateDisableFloatingCounts = false;
            TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision = false;
            TitanOrbitDebugFlags.IsolateDisableShipSoftTrack = false;
            TitanOrbitDebugFlags.IsolateDisableGemBurst = false;
        }
    }
}
