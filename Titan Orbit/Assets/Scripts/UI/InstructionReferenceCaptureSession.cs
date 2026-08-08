using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TitanOrbit.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Game
{
    /// <summary>
    /// In-game debug tool: press <b>F8</b>/<b>F9</b> after Join Team to gather reference plates for
    /// rebuilding <c>Resources/InstructionScreens/</c>. Writes PNGs + <c>manifest.json</c> under
    /// <c>Titan Orbit/Captures/InstructionRefs/&lt;timestamp&gt;/</c>.
    /// <para>
    /// Shot plan (what later art needs):
    /// <list type="bullet">
    /// <item><b>objective</b> — expanded full-map minimap (all planets + territory triangles), plus a world pullback</item>
    /// <item><b>planet_ships</b> — several distinct in-game planets (different surfaces) + cross-family catalog thumbs</item>
    /// <item><b>transport</b> — yellow people-transport flight orbs (not defense turrets)</item>
    /// <item><b>mining</b> — asteroid field with red gems (simple frame)</item>
    /// <item><b>upgrades</b> — moon dock / orbit station UI</item>
    /// </list>
    /// During each screenshot the status banner and most gameplay HUD canvases are hidden so plates stay clean.
    /// </para>
    /// <para>
    /// Client presentation only. Discovers subjects from hybrid GameObject proxies
    /// (<c>HomePlanetProxy</c>, <c>ShipTagProxy</c>, …) — never full ECS map-body / ship entity gathers
    /// (Windows late-join Crash!!!). Skips start only while
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> is true.
    /// Do <b>not</b> also require <see cref="ClientJoinSettleCache.ShouldSkipMapBodyQueries"/> —
    /// that stays true all session via TransformQuarantine and would permanently block F8.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(70200)]
    public sealed class InstructionReferenceCaptureSession : MonoBehaviour
    {
        /// <summary>One row written into <c>manifest.json</c> for each saved plate.</summary>
        [Serializable]
        sealed class ManifestEntry
        {
            /// <summary>File name relative to the session output folder.</summary>
            public string file;

            /// <summary>Short human label (what was framed).</summary>
            public string subject;

            /// <summary>
            /// Which instruction card this plate is meant for:
            /// objective / transport / mining / upgrades / planet_ships / reference.
            /// </summary>
            public string instructionCard;

            /// <summary>auto | guided | ship_catalog</summary>
            public string phase;
        }

        /// <summary>Root object for <c>manifest.json</c> serialization.</summary>
        [Serializable]
        sealed class ManifestRoot
        {
            /// <summary>ISO-ish session id (folder name).</summary>
            public string sessionId;

            /// <summary>UTC time when the session started.</summary>
            public string startedUtc;

            /// <summary>Absolute path to the output folder.</summary>
            public string outputDirectory;

            /// <summary>
            /// All plates written this session.
            /// [UNITY] JsonUtility serializes arrays of [Serializable] types — not List&lt;T&gt;.
            /// </summary>
            public ManifestEntry[] entries;
        }

        /// <summary>One guided step the player confirms with F8 when the frame looks good.</summary>
        struct GuidedStep
        {
            public string FileName;
            public string Subject;
            public string InstructionCard;
            public string Prompt;

            /// <summary>When true, keep orbit-station UI visible during the capture (upgrades plate).</summary>
            public bool KeepOrbitStationHud;

            /// <summary>When true, keep the minimap visible during the capture.</summary>
            public bool KeepMinimapHud;
        }

        /// <summary>Saved CanvasGroup state so we can restore gameplay HUD after a clean plate.</summary>
        struct HiddenHudState
        {
            public CanvasGroup Group;
            public bool AddedGroup;
            public float Alpha;
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        /// <summary>Idle → auto tour coroutine → wait for guided F8s → done.</summary>
        enum Phase
        {
            Idle,
            AutoTour,
            Guided,
            Done
        }

        /// <summary>Frames to wait after moving the camera so the Game View finishes drawing.</summary>
        const int SettleFramesAfterCameraMove = 3;

        /// <summary>
        /// How many distinct ship families to sample for planet-ships catalog refs.
        /// [TITAN-ORBIT] PlanetShipFamilyConfig maps one family per planet — the card must not
        /// show only AstroEagle chassis variants.
        /// </summary>
        const int MaxShipFamilyCatalogCopies = 8;

        Phase _phase = Phase.Idle;
        string _statusLine = "Instruction capture: press F8 or F9 after Join Team (flying) to start.";
        string _outputDir;
        string _sessionId;
        ManifestRoot _manifest;
        CameraFollowEcs _follow;
        bool _followWasEnabled;
        Coroutine _sessionRoutine;
        int _guidedIndex;
        bool _guidedCaptureRequested;
        bool _cancelRequested;
        readonly List<GuidedStep> _guidedSteps = new List<GuidedStep>();
        readonly List<Transform> _planetScratch = new List<Transform>(64);
        readonly List<Transform> _asteroidScratch = new List<Transform>(256);
        readonly List<Transform> _gemScratch = new List<Transform>(64);
        readonly List<Transform> _transportScratch = new List<Transform>(32);
        readonly List<Transform> _droneScratch = new List<Transform>(64);

        /// <summary>Accumulates manifest rows; copied to <see cref="ManifestRoot.entries"/> on write.</summary>
        readonly List<ManifestEntry> _entries = new List<ManifestEntry>(64);

        /// <summary>When true, OnGUI status banner is skipped so it is not baked into screenshots.</summary>
        bool _hideStatusBannerForCapture;

        /// <summary>HUD canvas groups faded out for the current capture frame.</summary>
        readonly List<HiddenHudState> _hiddenHud = new List<HiddenHudState>(32);

        /// <summary>
        /// [UNITY] Auto-install after scene load so Play Mode always has the hotkey —
        /// same pattern as <see cref="ClientStutterIsolator"/>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            // --- Dedicated / headless servers have no Game View to capture ---
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (FindAnyObjectByType<InstructionReferenceCaptureSession>() != null)
                return;

            // Prefer hitching onto the session manager so we ride its DontDestroyOnLoad lifetime.
            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<InstructionReferenceCaptureSession>();
                Debug.Log("[InstructionCapture] Installed on TitanOrbitSessionManager — press F8 (or F9) after Join Team.");
                return;
            }

            var go = new GameObject("InstructionReferenceCaptureSession");
            DontDestroyOnLoad(go);
            go.AddComponent<InstructionReferenceCaptureSession>();
            Debug.Log("[InstructionCapture] Installed (standalone DDOL) — press F8 (or F9) after Join Team.");
        }

        /// <summary>
        /// [UNITY] Per-frame hotkey + status. F8/F9 starts / confirms guided; Esc or Shift+F8 cancels.
        /// </summary>
        void Update()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // --- Read keys (Input System preferred; legacy fallback if Keyboard.current is null) ---
            bool f8 = WasCaptureKeyPressed(out bool shiftHeld, out bool escapePressed);

            // --- Cancel ---
            if (_phase != Phase.Idle && _phase != Phase.Done)
            {
                if (escapePressed || (shiftHeld && f8))
                {
                    RequestCancel("Cancelled by player.");
                    return;
                }
            }

            // --- F8/F9: start session or confirm guided plate ---
            // Ignore Shift+F8 here (that combo is cancel while a session is active).
            if (!f8 || shiftHeld)
                return;

            Debug.Log($"[InstructionCapture] Capture key — phase={_phase}");

            if (_phase == Phase.Idle || _phase == Phase.Done)
            {
                TryBeginSession();
                return;
            }

            if (_phase == Phase.Guided)
                _guidedCaptureRequested = true;
        }

        /// <summary>
        /// True on the frame the player presses F8 or F9.
        /// F9 is an alternate because the Editor sometimes eats F8 when Game View is unfocused.
        /// </summary>
        /// <param name="shiftHeld">True if either Shift is down (used for Shift+F8 cancel).</param>
        /// <param name="escapePressed">True if Escape was pressed this frame.</param>
        static bool WasCaptureKeyPressed(out bool shiftHeld, out bool escapePressed)
        {
            shiftHeld = false;
            escapePressed = false;

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                shiftHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                escapePressed = keyboard.escapeKey.wasPressedThisFrame;
                // F8 primary; F9 alternate if the Editor steals F8.
                return keyboard.f8Key.wasPressedThisFrame || keyboard.f9Key.wasPressedThisFrame;
            }

            // [UNITY] Legacy Input — only when Input System has no Keyboard device.
            // Fully qualify: TitanOrbit.Input is a namespace and would shadow UnityEngine.Input.
            shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            escapePressed = UnityEngine.Input.GetKeyDown(KeyCode.Escape);
            return UnityEngine.Input.GetKeyDown(KeyCode.F8) || UnityEngine.Input.GetKeyDown(KeyCode.F9);
        }

        /// <summary>[UNITY] On-screen status so you know which plate is next without looking at the Console.</summary>
        void OnGUI()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            // --- Never bake the status strip into reference plates ---
            if (_hideStatusBannerForCapture)
                return;

            // Always show a thin hint while idle so the feature is discoverable.
            const float pad = 10f;
            float width = Mathf.Min(720f, Screen.width - pad * 2f);
            float height = _phase == Phase.Idle || _phase == Phase.Done ? 36f : 72f;
            var rect = new Rect(pad, Screen.height - height - pad, width, height);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 6, 6)
            };
            GUI.Label(rect, _statusLine, style);
            GUI.color = prev;
        }

        /// <summary>
        /// Validates settle/backlog gates, creates the output folder, and starts the auto-tour coroutine.
        /// </summary>
        void TryBeginSession()
        {
            // --- Join-safety gate (ships Instantiates only) ---
            // [TITAN-ORBIT] ShouldSkipShipEntityQueries = Settling OR GhostSpawnBacklog OR TeamChoice hold.
            // Intentional: do NOT fold in ShouldSkipMapBodyQueries — TransformQuarantine is session-long
            // on Windows, so that helper stays true forever after join and would make F8 a no-op.
            // We only walk hybrid GameObject proxies (no ECS map gathers), so quarantine is fine.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                _statusLine =
                    "Instruction capture: wait until ship Instantiates finish, then press F8 again.\n" +
                    $"(Settling={ClientJoinSettleCache.Settling} GhostSpawnBacklog={ClientJoinSettleCache.GhostSpawnBacklog})";
                Debug.LogWarning(
                    "[InstructionCapture] Blocked — ShouldSkipShipEntityQueries is true " +
                    $"(Settling={ClientJoinSettleCache.Settling}, " +
                    $"GhostSpawnBacklog={ClientJoinSettleCache.GhostSpawnBacklog}).");
                return;
            }

            if (_sessionRoutine != null)
                return;

            _cancelRequested = false;
            _guidedCaptureRequested = false;
            _guidedIndex = 0;
            BuildGuidedSteps();

            _sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            // Application.dataPath = .../Titan Orbit/Assets → sibling Captures folder under the Unity project.
            _outputDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures", "InstructionRefs", _sessionId));
            Directory.CreateDirectory(_outputDir);

            _entries.Clear();
            _manifest = new ManifestRoot
            {
                sessionId = _sessionId,
                startedUtc = DateTime.UtcNow.ToString("o"),
                outputDirectory = _outputDir,
                entries = Array.Empty<ManifestEntry>()
            };

            _statusLine = $"Instruction capture: auto tour… → {_outputDir}";
            Debug.Log($"[InstructionCapture] Session start → {_outputDir}");
            _sessionRoutine = StartCoroutine(RunSessionCoroutine());
        }

        /// <summary>
        /// Builds the guided prompt queue — rare moments auto-tour cannot force.
        /// Ordered for the five instruction cards; prompts tell the player exactly what to frame.
        /// </summary>
        void BuildGuidedSteps()
        {
            _guidedSteps.Clear();

            // --- transport: yellow people-transport flight orbs only (never defense turrets) ---
            _guidedSteps.Add(new GuidedStep
            {
                FileName = "guided_transport.png",
                Subject = "Yellow people transports mid-flight (not turrets)",
                InstructionCard = "transport",
                Prompt =
                    "TRANSPORT: Orbit a friendly planet until YELLOW people-transport spheres fly ship↔planet. " +
                    "Frame those yellow orbs (not defense pads/turrets), then F8."
            });

            // --- mining: red gems in a simple asteroid shot ---
            _guidedSteps.Add(new GuidedStep
            {
                FileName = "guided_mining_red_gems.png",
                Subject = "Asteroid field with red gems only focus",
                InstructionCard = "mining",
                Prompt =
                    "MINING: Break asteroids until RED gems are floating. Frame a simple shot — asteroids + red gems " +
                    "(avoid busy HUD if you can). Then F8."
            });

            // --- upgrades: orbit station UI ---
            _guidedSteps.Add(new GuidedStep
            {
                FileName = "guided_orbit_station.png",
                Subject = "Moon dock / orbit station upgrade UI",
                InstructionCard = "upgrades",
                KeepOrbitStationHud = true,
                Prompt = "UPGRADES: Open the moon dock / orbit station upgrade UI, then F8."
            });

            // --- planet_ships: distinct in-game planets (different surfaces / families) ---
            _guidedSteps.Add(new GuidedStep
            {
                FileName = "guided_planet_ships_a.png",
                Subject = "Non-home planet + ship (family A surface)",
                InstructionCard = "planet_ships",
                Prompt =
                    "PLANET SHIPS A: Fly to a NON-home planet with a DIFFERENT surface look than home. " +
                    "Frame the whole planet large (ship optional in frame), then F8."
            });
            _guidedSteps.Add(new GuidedStep
            {
                FileName = "guided_planet_ships_b.png",
                Subject = "Second distinct planet surface + ship",
                InstructionCard = "planet_ships",
                Prompt =
                    "PLANET SHIPS B: Visit another planet that looks different from the last one. " +
                    "Frame that planet large, then F8 (or F8 now to skip)."
            });
        }

        /// <summary>Flags cancel; the running coroutine restores the camera and writes a partial manifest.</summary>
        void RequestCancel(string reason)
        {
            _cancelRequested = true;
            _statusLine = $"Instruction capture: {reason}";
            Debug.LogWarning($"[InstructionCapture] {reason}");
        }

        /// <summary>
        /// Full session: copy ship catalog refs → auto tour plates → guided F8 plates → manifest.json.
        /// </summary>
        IEnumerator RunSessionCoroutine()
        {
            _phase = Phase.AutoTour;

            // --- Supplemental AstroEagle theatrical thumbs (no in-match purchase needed) ---
            CopyTheatricalShipRefs();

            // --- Pause follow so we can aim the gameplay camera ---
            _follow = FindAnyObjectByType<CameraFollowEcs>();
            _followWasEnabled = _follow != null && _follow.enabled;
            if (_follow != null)
                _follow.enabled = false;

            yield return RunAutoTour();

            // --- Restore follow before guided play (player flies again) ---
            RestoreFollowCamera();

            if (_cancelRequested)
            {
                FinishSession(cancelled: true);
                yield break;
            }

            // --- Guided phase ---
            _phase = Phase.Guided;
            for (_guidedIndex = 0; _guidedIndex < _guidedSteps.Count; _guidedIndex++)
            {
                if (_cancelRequested)
                    break;

                GuidedStep step = _guidedSteps[_guidedIndex];
                _guidedCaptureRequested = false;
                _statusLine =
                    $"Guided {_guidedIndex + 1}/{_guidedSteps.Count}: {step.Prompt}\n" +
                    "F8 = capture · Esc / Shift+F8 = cancel";

                // Wait until the player confirms the frame (or cancels).
                while (!_guidedCaptureRequested && !_cancelRequested)
                    yield return null;

                if (_cancelRequested)
                    break;

                yield return CaptureGameView(
                    step.FileName,
                    step.Subject,
                    step.InstructionCard,
                    "guided",
                    keepMinimap: step.KeepMinimapHud,
                    keepOrbitStation: step.KeepOrbitStationHud);
            }

            FinishSession(cancelled: _cancelRequested);
        }

        /// <summary>
        /// Auto-tour plates for instruction art. Order matches what later cards need most:
        /// full-map objective first, then distinct planets, ship, mining refs, optional transport.
        /// </summary>
        IEnumerator RunAutoTour()
        {
            // --- Discover presentation proxies once (GameObject names only) ---
            RefreshProxyCaches();

            Vector3 shipPos = ResolveLocalShipWorldPosition();
            // [UNITY] Fully qualify — TitanOrbit.Camera is a namespace and would shadow Camera otherwise.
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[InstructionCapture] No Camera.main — skipping auto tour framing.");
                yield break;
            }

            // =========================================================
            // 1) OBJECTIVE — expanded full map (all planets + triangles)
            // =========================================================
            var minimap = FindAnyObjectByType<MinimapController>();
            bool wasExpanded = false;
            if (minimap != null)
            {
                wasExpanded = minimap.IsExpanded;
                minimap.SetExpanded(true);
                for (int i = 0; i < SettleFramesAfterCameraMove + 2; i++)
                    yield return null;

                // Keep minimap; hide other HUD so the plate is mostly the map.
                yield return CaptureGameView(
                    "01_objective_full_map.png",
                    "Expanded minimap — all planets + territory triangles",
                    "objective",
                    "auto",
                    keepMinimap: true,
                    keepOrbitStation: false);

                minimap.SetExpanded(wasExpanded);
            }
            else
                Debug.LogWarning("[InstructionCapture] MinimapController not found — skipped full-map objective plate.");

            if (_cancelRequested) yield break;

            // =========================================================
            // 2) OBJECTIVE — world pullback with territory triangles
            // =========================================================
            Transform home = FindNearestNamed(_planetScratch, shipPos, preferName: "HomePlanetProxy");
            if (home == null)
                home = FindNearest(_planetScratch, shipPos);

            if (_planetScratch.Count >= 2)
            {
                GetNearestClusterCentroid(_planetScratch, shipPos, maxCount: 6, out Vector3 centroid, out float radius);
                FrameTopDown(cam, centroid, Mathf.Clamp(radius * 2.8f, 60f, 280f));
                yield return CaptureGameView(
                    "02_objective_territory_world.png",
                    "World pullback — planets + territory fill",
                    "objective",
                    "auto");
            }
            else if (home != null)
            {
                FrameTopDown(cam, home.position, 160f);
                yield return CaptureGameView(
                    "02_objective_territory_world.png",
                    "Wide home pullback (territory if present)",
                    "objective",
                    "auto");
            }

            if (_cancelRequested) yield break;

            // =========================================================
            // 3) PLANET SHIPS — several distinct in-game planets
            // =========================================================
            yield return CaptureDistinctPlanets(cam, shipPos, home);

            if (_cancelRequested) yield break;

            // =========================================================
            // 4) Local ship hull (family catalog still copied separately)
            // =========================================================
            Transform ship = EcsWorldVisualizer.LocalPlayerShipVisualRoot;
            if (ship == null)
                ship = FindNearestNamed(null, shipPos, preferName: "ShipTagProxy", alsoScanAll: true);
            if (ship != null)
            {
                FrameTheatrical(cam, ship);
                yield return CaptureGameView(
                    "06_local_ship.png",
                    "Local player ship hull",
                    "planet_ships",
                    "auto");
            }

            if (_cancelRequested) yield break;

            // =========================================================
            // 5) MINING — asteroid field (simple)
            // =========================================================
            Transform asteroid = FindNearest(_asteroidScratch, shipPos);
            if (asteroid != null)
            {
                GetNearestClusterCentroid(_asteroidScratch, shipPos, maxCount: 10, out Vector3 aCentroid, out float aRadius);
                FrameTopDown(cam, aCentroid, Mathf.Max(aRadius * 2.2f, 22f));
                yield return CaptureGameView(
                    "07_asteroid_field.png",
                    "Asteroid field (simple mining plate)",
                    "mining",
                    "auto");
            }

            if (_cancelRequested) yield break;

            // =========================================================
            // 6) MINING — red gems close-up if any gem proxies exist
            // =========================================================
            Transform gem = FindNearest(_gemScratch, shipPos);
            if (gem != null)
            {
                // Prefer a small cluster of gems for a clean red-gem plate.
                GetNearestClusterCentroid(_gemScratch, shipPos, maxCount: 8, out Vector3 gCentroid, out float gRadius);
                FrameTopDown(cam, gCentroid, Mathf.Clamp(gRadius * 4f, 12f, 40f));
                yield return CaptureGameView(
                    "08_red_gems.png",
                    "Gem cluster close-up (prefer red gems in frame)",
                    "mining",
                    "auto");
            }

            if (_cancelRequested) yield break;

            // =========================================================
            // 7) TRANSPORT bonus — only PeopleTransportProxy (never turrets)
            // =========================================================
            Transform transport = FindNearestPeopleTransport(shipPos);
            if (transport != null)
            {
                FrameTheatrical(cam, transport);
                yield return CaptureGameView(
                    "09_bonus_people_transport.png",
                    "PeopleTransportProxy mid-flight (bonus auto)",
                    "transport",
                    "auto");
            }
        }

        /// <summary>
        /// Frames up to three well-spaced planet proxies for planet_ships art
        /// (different surfaces / families — not three angles of the same world).
        /// </summary>
        IEnumerator CaptureDistinctPlanets(UnityEngine.Camera cam, Vector3 shipPos, Transform home)
        {
            // Sort planets by distance from ship; pick ones that are far from each other.
            var scored = new List<(float sq, Transform t)>(_planetScratch.Count);
            for (int i = 0; i < _planetScratch.Count; i++)
            {
                Transform t = _planetScratch[i];
                if (t == null)
                    continue;
                scored.Add((PlanarSq(shipPos, t.position), t));
            }

            scored.Sort((a, b) => a.sq.CompareTo(b.sq));

            var picked = new List<Transform>(3);
            const float minSeparationSq = 35f * 35f;
            for (int i = 0; i < scored.Count && picked.Count < 3; i++)
            {
                Transform candidate = scored[i].t;
                bool farEnough = true;
                for (int p = 0; p < picked.Count; p++)
                {
                    if (PlanarSq(candidate.position, picked[p].position) < minSeparationSq)
                    {
                        farEnough = false;
                        break;
                    }
                }

                if (!farEnough)
                    continue;

                picked.Add(candidate);
            }

            // Always try to include home as planet_01 if we have it and room.
            if (home != null && !picked.Contains(home) && picked.Count < 3)
                picked.Insert(0, home);

            string[] names =
            {
                "03_planet_a.png",
                "04_planet_b.png",
                "05_planet_c.png"
            };

            for (int i = 0; i < picked.Count && i < names.Length; i++)
            {
                if (_cancelRequested)
                    yield break;

                Transform planet = picked[i];
                float r = EstimateSubjectRadius(planet);
                // Large planet in frame — this is the surface look for planet_ships cards.
                FrameTopDown(cam, planet.position, Mathf.Clamp(r * 3.2f, 18f, 70f));
                string label = planet.name == "HomePlanetProxy"
                    ? "Home planet (in-game surface)"
                    : $"In-game planet surface ({planet.name})";
                yield return CaptureGameView(names[i], label, "planet_ships", "auto");
            }

            if (picked.Count == 0)
                Debug.LogWarning("[InstructionCapture] No planet proxies for planet_ships auto plates.");
        }

        /// <summary>
        /// Nearest people-transport flight proxy only — never FighterDrone / defense pad names.
        /// </summary>
        Transform FindNearestPeopleTransport(Vector3 from)
        {
            Transform best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _transportScratch.Count; i++)
            {
                Transform t = _transportScratch[i];
                if (t == null)
                    continue;

                // [TITAN-ORBIT] PeopleTransportVisualApplier names flight GOs PeopleTransportProxy.
                // Skip anything that looks like a turret / pad / drone.
                string n = t.name;
                if (n.IndexOf("Defense", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Turret", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Drone", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Pad", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (n != "PeopleTransportProxy" &&
                    n != "PeopleTransportShip" &&
                    !n.StartsWith("PeopleTransport", System.StringComparison.Ordinal))
                    continue;

                float sq = PlanarSq(from, t.position);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = t;
                }
            }

            return best;
        }

        /// <summary>
        /// Scans active Transforms by hybrid proxy name. Presentation-safe GameObject walk only.
        /// </summary>
        void RefreshProxyCaches()
        {
            _planetScratch.Clear();
            _asteroidScratch.Clear();
            _gemScratch.Clear();
            _transportScratch.Clear();
            _droneScratch.Clear();

            // [HYBRID] Proxies are created by EcsWorldVisualizer / PeopleTransportVisualApplier / DroneSwarmVisualDriver.
            // FindObjectsByType on Transforms walks the Unity scene hierarchy — not an ECS map-body query.
            Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                    continue;

                string n = t.name;
                if (n == "HomePlanetProxy" || n == "PlanetTagProxy")
                    _planetScratch.Add(t);
                else if (n == "AsteroidTagProxy")
                    _asteroidScratch.Add(t);
                else if (n == "GemTagProxy")
                    _gemScratch.Add(t);
                else if (n == "PeopleTransportProxy" || n == "PeopleTransportShip")
                    _transportScratch.Add(t);
                else if (n.StartsWith("FighterDrone_", StringComparison.Ordinal) ||
                         n.StartsWith("MiningDrone_", StringComparison.Ordinal) ||
                         n.StartsWith("ShieldDrone_", StringComparison.Ordinal) ||
                         n.Contains("FighterDrone") ||
                         n.Contains("MiningDrone") ||
                         n.Contains("ShieldDrone"))
                    _droneScratch.Add(t);
            }
        }

        /// <summary>Local ship world position from the hybrid visual root, else Camera.main, else origin.</summary>
        Vector3 ResolveLocalShipWorldPosition()
        {
            if (EcsWorldVisualizer.LocalPlayerShipVisualRoot != null)
                return EcsWorldVisualizer.LocalPlayerShipVisualRoot.position;

            if (UnityEngine.Camera.main != null)
                return UnityEngine.Camera.main.transform.position;

            return Vector3.zero;
        }

        /// <summary>Nearest transform in <paramref name="list"/> to <paramref name="from"/> (XZ distance).</summary>
        static Transform FindNearest(List<Transform> list, Vector3 from)
        {
            if (list == null || list.Count == 0)
                return null;

            Transform best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                Transform t = list[i];
                if (t == null)
                    continue;
                float sq = PlanarSq(from, t.position);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = t;
                }
            }

            return best;
        }

        /// <summary>
        /// Prefer a specific GameObject name; optionally fall back to a full hierarchy scan for that name.
        /// </summary>
        Transform FindNearestNamed(List<Transform> list, Vector3 from, string preferName, bool alsoScanAll = false)
        {
            Transform best = null;
            float bestSq = float.MaxValue;

            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Transform t = list[i];
                    if (t == null || t.name != preferName)
                        continue;
                    float sq = PlanarSq(from, t.position);
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = t;
                    }
                }
            }

            if (best != null || !alsoScanAll)
                return best;

            Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t.name != preferName)
                    continue;
                float sq = PlanarSq(from, t.position);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = t;
                }
            }

            return best;
        }

        /// <summary>Centroid + bounding radius of the nearest <paramref name="maxCount"/> subjects on XZ.</summary>
        static void GetNearestClusterCentroid(
            List<Transform> list,
            Vector3 from,
            int maxCount,
            out Vector3 centroid,
            out float radius)
        {
            centroid = from;
            radius = 20f;
            if (list == null || list.Count == 0)
                return;

            // Sort indices by distance (small N — planets/asteroid subset).
            var scored = new List<(float sq, Transform t)>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Transform t = list[i];
                if (t == null)
                    continue;
                scored.Add((PlanarSq(from, t.position), t));
            }

            scored.Sort((a, b) => a.sq.CompareTo(b.sq));
            int take = Mathf.Min(maxCount, scored.Count);
            if (take <= 0)
                return;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < take; i++)
                sum += scored[i].t.position;
            centroid = sum / take;

            float maxR = 1f;
            for (int i = 0; i < take; i++)
            {
                float d = Mathf.Sqrt(PlanarSq(centroid, scored[i].t.position));
                if (d > maxR)
                    maxR = d;
            }

            radius = maxR + 8f;
        }

        /// <summary>XZ squared distance (ignore Y — top-down play plane).</summary>
        static float PlanarSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>Rough world radius from renderers under the proxy (fallback constants if none).</summary>
        static float EstimateSubjectRadius(Transform root)
        {
            if (root == null)
                return 10f;

            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
                return 12f;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);

            return Mathf.Max(b.extents.x, b.extents.z, b.extents.y, 4f);
        }

        /// <summary>Top-down gameplay-style framing above a world point.</summary>
        static void FrameTopDown(UnityEngine.Camera cam, Vector3 lookAt, float height)
        {
            if (cam == null)
                return;

            // Allow higher pullbacks for objective territory plates.
            height = Mathf.Clamp(height, 12f, 320f);
            cam.transform.position = new Vector3(lookAt.x, lookAt.y + height, lookAt.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>Front-right elevated framing (reads silhouettes better for ships / transports).</summary>
        static void FrameTheatrical(UnityEngine.Camera cam, Transform subject)
        {
            if (cam == null || subject == null)
                return;

            float r = EstimateSubjectRadius(subject);
            Vector3 look = subject.position + Vector3.up * (r * 0.15f);
            // Match the theatrical menu preview feel: slightly in front and to the side.
            Vector3 offset = new Vector3(r * 1.1f, r * 1.6f, -r * 1.4f);
            cam.transform.position = look + offset;
            cam.transform.rotation = Quaternion.LookRotation((look - cam.transform.position).normalized, Vector3.up);
        }

        /// <summary>
        /// Waits for the Game View to settle, hides HUD/status (so plates stay clean), then writes a PNG.
        /// </summary>
        /// <param name="keepMinimap">Leave minimap canvases visible (full-map objective plate).</param>
        /// <param name="keepOrbitStation">Leave orbit-station UI visible (upgrades plate).</param>
        IEnumerator CaptureGameView(
            string fileName,
            string subject,
            string instructionCard,
            string phase,
            bool keepMinimap = false,
            bool keepOrbitStation = false)
        {
            if (_cancelRequested)
                yield break;

            _statusLine = $"Capturing {fileName}…";

            // --- Let LateUpdate / UI / Shapes ImmediateMode draw the new framing ---
            for (int i = 0; i < SettleFramesAfterCameraMove; i++)
                yield return null;

            // --- Clean plate: no status banner, most gameplay HUD faded out ---
            _hideStatusBannerForCapture = true;
            HideGameplayHudForCapture(keepMinimap, keepOrbitStation);

            // One more frame so CanvasGroup alpha=0 is applied before the grab.
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                // [UNITY] CaptureScreenshotAsTexture — reads the Game View after EndOfFrame.
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex == null)
                {
                    Debug.LogWarning($"[InstructionCapture] CaptureScreenshotAsTexture returned null for {fileName}");
                    yield break;
                }

                string path = Path.Combine(_outputDir, fileName);
                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(path, png);

                _entries.Add(new ManifestEntry
                {
                    file = fileName,
                    subject = subject,
                    instructionCard = instructionCard,
                    phase = phase
                });

                Debug.Log($"[InstructionCapture] Wrote {path}");
            }
            finally
            {
                if (tex != null)
                    Destroy(tex);

                RestoreGameplayHudAfterCapture();
                _hideStatusBannerForCapture = false;
            }
        }

        /// <summary>
        /// Fades gameplay Canvas roots for a clean plate.
        /// Pattern mirrors <see cref="OrbitMenuHudSuppressor"/> (CanvasGroup alpha = 0).
        /// </summary>
        void HideGameplayHudForCapture(bool keepMinimap, bool keepOrbitStation)
        {
            RestoreGameplayHudAfterCapture();

            Transform orbitRoot = null;
            if (keepOrbitStation)
            {
                // Prefer an already-open station UI — do not GetOrCreate() (that would spawn a blank menu).
                var station = FindAnyObjectByType<OrbitStationUI>();
                if (station != null)
                    orbitRoot = station.transform;
            }

            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null)
                    continue;

                // Keep expanded minimap when capturing the objective full-map plate.
                if (keepMinimap && canvas.GetComponentInParent<MinimapController>() != null)
                    continue;
                if (keepMinimap && canvas.GetComponent<MinimapController>() != null)
                    continue;

                // Keep moon-dock / orbit station when capturing upgrades.
                if (orbitRoot != null)
                {
                    if (canvas.transform == orbitRoot ||
                        canvas.transform.IsChildOf(orbitRoot) ||
                        orbitRoot.IsChildOf(canvas.transform))
                        continue;
                }

                PushHudHide(canvas.gameObject);
            }
        }

        /// <summary>Adds/fades a CanvasGroup on <paramref name="root"/> and remembers prior state.</summary>
        void PushHudHide(GameObject root)
        {
            if (root == null)
                return;

            var group = root.GetComponent<CanvasGroup>();
            bool added = group == null;
            if (group == null)
                group = root.AddComponent<CanvasGroup>();

            _hiddenHud.Add(new HiddenHudState
            {
                Group = group,
                AddedGroup = added,
                Alpha = group.alpha,
                Interactable = group.interactable,
                BlocksRaycasts = group.blocksRaycasts,
            });

            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        /// <summary>Restores every CanvasGroup we faded for the last capture.</summary>
        void RestoreGameplayHudAfterCapture()
        {
            for (int i = 0; i < _hiddenHud.Count; i++)
            {
                HiddenHudState state = _hiddenHud[i];
                if (state.Group == null)
                    continue;

                state.Group.alpha = state.Alpha;
                state.Group.interactable = state.Interactable;
                state.Group.blocksRaycasts = state.BlocksRaycasts;
                if (state.AddedGroup)
                    Destroy(state.Group);
            }

            _hiddenHud.Clear();
        }

        /// <summary>
        /// Copies one chassis preview from each distinct ship family under
        /// <c>Assets/Prefabs/Ships/*/MenuPreviews…</c> so planet-ships art can show
        /// different <see cref="TitanOrbit.Data.ShipFamilyDefinition"/> silhouettes
        /// (not multiple AstroEagle upgrade-tree tiers).
        /// </summary>
        void CopyTheatricalShipRefs()
        {
            // --- Roots to search (theatrical preferred, then standard MenuPreviews) ---
            string shipsRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "Prefabs", "Ships"));
            if (!Directory.Exists(shipsRoot))
            {
                Debug.LogWarning($"[InstructionCapture] Ships prefab root missing: {shipsRoot}");
                return;
            }

            // Prefer families that appear in PlanetShipFamilyConfig order when folders match.
            string[] preferredFamilies =
            {
                "AstroEagle", "CosmicShark", "ForceBadger", "GalaxyRaptor",
                "HyperFalcon", "LightFox", "MeteorMantis", "NightAye",
                "ProtonLegacy", "SpaceExcalibur", "StarForce", "StriderOx"
            };

            int copied = 0;
            for (int f = 0; f < preferredFamilies.Length && copied < MaxShipFamilyCatalogCopies; f++)
            {
                string family = preferredFamilies[f];
                string src = FindFamilyChassisPreviewPng(shipsRoot, family);
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                    continue;

                string destName = $"ship_ref_{family}_01.png";
                string dest = Path.Combine(_outputDir, destName);
                try
                {
                    File.Copy(src, dest, overwrite: true);
                    _entries.Add(new ManifestEntry
                    {
                        file = destName,
                        subject = $"{family} family catalog preview",
                        instructionCard = "planet_ships",
                        phase = "ship_catalog"
                    });
                    copied++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[InstructionCapture] Failed to copy {src}: {ex.Message}");
                }
            }

            Debug.Log($"[InstructionCapture] Copied {copied} cross-family ship refs (one chassis each).");
        }

        /// <summary>
        /// Finds a single representative PNG for <paramref name="familyFolderName"/> —
        /// theatrical TeamA first, then MenuPreviews/TeamA, then any color folder *_01.png.
        /// </summary>
        static string FindFamilyChassisPreviewPng(string shipsRoot, string familyFolderName)
        {
            string familyRoot = Path.Combine(shipsRoot, familyFolderName);
            if (!Directory.Exists(familyRoot))
                return null;

            // 1) Theatrical TeamA (best silhouette framing when present).
            string theatrical = Path.Combine(familyRoot, "MenuPreviewsTheatrical", "TeamA");
            string hit = FirstMatchingPng(theatrical, familyFolderName + "_01.png", familyFolderName + "_*.png");
            if (hit != null)
                return hit;

            // 2) Standard MenuPreviews/TeamA.
            string teamA = Path.Combine(familyRoot, "MenuPreviews", "TeamA");
            hit = FirstMatchingPng(teamA, familyFolderName + "_01.png", familyFolderName + "_*.png");
            if (hit != null)
                return hit;

            // 3) Color-named folders (Blue/Green/Orange/…) used by several families.
            string menuPreviews = Path.Combine(familyRoot, "MenuPreviews");
            if (!Directory.Exists(menuPreviews))
                return null;

            string[] colorDirs = Directory.GetDirectories(menuPreviews);
            Array.Sort(colorDirs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < colorDirs.Length; i++)
            {
                hit = FirstMatchingPng(colorDirs[i], familyFolderName + "_01.png", familyFolderName + "_*.png");
                if (hit != null)
                    return hit;
            }

            return null;
        }

        /// <summary>Returns the first existing PNG under <paramref name="dir"/> matching exact then glob.</summary>
        static string FirstMatchingPng(string dir, string exactFileName, string glob)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            string exact = Path.Combine(dir, exactFileName);
            if (File.Exists(exact))
                return exact;

            string[] files = Directory.GetFiles(dir, glob);
            if (files == null || files.Length == 0)
                return null;

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files[0];
        }

        /// <summary>Re-enables <see cref="CameraFollowEcs"/> if we disabled it for the auto tour.</summary>
        void RestoreFollowCamera()
        {
            if (_follow != null && _followWasEnabled)
                _follow.enabled = true;
        }

        /// <summary>Writes manifest.json, restores camera, and returns to Idle/Done status.</summary>
        void FinishSession(bool cancelled)
        {
            RestoreFollowCamera();
            RestoreGameplayHudAfterCapture();
            _hideStatusBannerForCapture = false;

            if (_manifest != null && !string.IsNullOrEmpty(_outputDir))
            {
                try
                {
                    // [UNITY] JsonUtility needs a concrete array field — flush the working list now.
                    _manifest.entries = _entries.ToArray();
                    string json = JsonUtility.ToJson(_manifest, prettyPrint: true);
                    File.WriteAllText(Path.Combine(_outputDir, "manifest.json"), json, Encoding.UTF8);

                    // Also write a short handoff note for the follow-up art pass.
                    string readmePath = Path.Combine(_outputDir, "NEXT_STEPS.txt");
                    File.WriteAllText(
                        readmePath,
                        "Session complete — reference plates for InstructionScreens art.\n\n" +
                        "AUTO plates (typical):\n" +
                        "  01_objective_full_map.png     → instruction_objective (expanded minimap)\n" +
                        "  02_objective_territory_world  → instruction_objective (world pullback)\n" +
                        "  03–05_planet_*.png            → instruction_planet_ships (distinct surfaces)\n" +
                        "  06_local_ship.png             → instruction_planet_ships (hull)\n" +
                        "  07_asteroid_field.png         → instruction_mining\n" +
                        "  08_red_gems.png               → instruction_mining (red gems)\n" +
                        "  09_bonus_people_transport.png → instruction_transport (if transports nearby)\n" +
                        "  ship_ref_*                    → cross-family catalog thumbs\n\n" +
                        "GUIDED plates (player-framed):\n" +
                        "  guided_transport.png          → yellow people transports (NOT turrets)\n" +
                        "  guided_mining_red_gems.png    → asteroids + red gems\n" +
                        "  guided_orbit_station.png      → moon dock / upgrades UI\n" +
                        "  guided_planet_ships_a/b.png   → more distinct in-game planets\n\n" +
                        "Tell the Cursor agent to rebuild the five\n" +
                        "Assets/Resources/InstructionScreens/instruction_*.png cards using these\n" +
                        "PNGs as reference_image_paths (keep filenames + ~1536x1024 3:2 aspect).\n" +
                        "No extra text on the cards — cool game images only.\n",
                        Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[InstructionCapture] Failed to write manifest: {ex.Message}");
                }
            }

            _phase = cancelled ? Phase.Idle : Phase.Done;
            _sessionRoutine = null;

            if (cancelled)
            {
                _statusLine =
                    $"Instruction capture cancelled. Partial output (if any): {_outputDir}\n" +
                    "Press F8 to start again.";
            }
            else
            {
                _statusLine =
                    $"Instruction capture done → {_outputDir}\n" +
                    "Tell the agent that folder path to rebuild InstructionScreens art. F8 = new session.";
                Debug.Log($"[InstructionCapture] Session complete → {_outputDir}");
            }
        }

        void OnDestroy()
        {
            // --- Safety: never leave follow camera or HUD stuck if Play Mode stops mid-capture ---
            RestoreFollowCamera();
            RestoreGameplayHudAfterCapture();
            _hideStatusBannerForCapture = false;
        }
    }
}
