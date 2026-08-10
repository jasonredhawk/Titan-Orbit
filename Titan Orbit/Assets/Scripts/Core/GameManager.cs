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
    /// Scene singleton that holds designer-tunable HUD options, debug flags for local play, and the
    /// Editor Test / Production multiplayer toggle. Lives on <c>NceGameRoot</c> in SampleScene
    /// (Inspector → Game Manager). Moon orbit ship-tree UI reads <see cref="DebugFreeShipUpgradeTree"/>
    /// so you can click any upgrade-tree node for free during testing. Also gates optional tools such as
    /// Instruction Image Capture (F8/F9 reference plates) and the stutter isolator. Publishes debug
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

        // [UNITY] Inspector toggle — when true, ship upgrade tree treats all nodes as free / clickable.
        [Header("Debug — Ship Upgrade Tree")]
        [Tooltip("When enabled, the moon orbit ship upgrade tree unlocks every node. Click any ship to try it for free (local Editor / development only).")]
        [SerializeField] bool debugFreeShipUpgradeTree;

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

        /// <summary>True when designers enabled free upgrades in the Inspector (client + local-host convenience).</summary>
        public bool DebugFreeShipUpgradeTree => debugFreeShipUpgradeTree;

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
        /// Safe static check used by moon orbit UI. Also true when the Shared flag was published
        /// (covers the brief window before Instance is set, and keeps UI/server in sync).
        /// </summary>
        public static bool IsDebugFreeShipUpgradeTreeActive =>
            TitanOrbitDebugFlags.FreeShipUpgradeTree
            || (Instance != null && Instance.debugFreeShipUpgradeTree);

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
                TitanOrbitDebugFlags.LogAsteroidDestroyPerf = false;
                TitanOrbitDebugFlags.InstructionImageCaptureEnabled = false;
                TitanOrbitDebugFlags.StutterIsolatorEnabled = false;
                ClearIsolationFlags();
                _hasPublishedShowSpeedometer = false;
            }
        }

        /// <summary>
        /// Copies Inspector fields into <see cref="TitanOrbitDebugFlags"/> for ECS / other assemblies,
        /// and notifies HUD listeners when Show Speedometer changes in Play Mode.
        /// </summary>
        public void PublishDebugFlags()
        {
            // [TITAN-ORBIT] ECS MoonOrbitStoreSystem cannot reference TitanOrbit.Core — Shared bridge.
            TitanOrbitDebugFlags.FreeShipUpgradeTree = debugFreeShipUpgradeTree;
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

            // --- HUD: speedometer on/off ---
            NotifyShowSpeedometerChangedIfNeeded();
        }

        /// <summary>
        /// Invokes <see cref="ShowSpeedometerChanged"/> in Play Mode when the value changes
        /// (or on the first publish after Awake so late subscribers can sync via Start instead).
        /// </summary>
        void NotifyShowSpeedometerChangedIfNeeded()
        {
            // [UNITY] Edit Mode OnValidate must not poke play-mode HUD components.
            if (!Application.isPlaying)
                return;

            if (_hasPublishedShowSpeedometer && _lastPublishedShowSpeedometer == showSpeedometer)
                return;

            _hasPublishedShowSpeedometer = true;
            _lastPublishedShowSpeedometer = showSpeedometer;
            ShowSpeedometerChanged?.Invoke(showSpeedometer);
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
