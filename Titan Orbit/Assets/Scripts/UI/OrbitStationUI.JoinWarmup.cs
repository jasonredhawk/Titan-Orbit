using TitanOrbit.Game;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Join-load Orbit Menu + minimap warmup. The loading screen ticks this while the galaxy
    /// overlay is up so chrome, the shared ship tree, every planet-family GEAR grid, and
    /// minimap blips exist <b>before</b> Join Team. First spawn used to hitch because those
    /// widgets built after the ship appeared.
    /// <para>
    /// Dedicated servers skip this — they have no Orbit Menu. A 15s timeout still dismisses
    /// loading if a prefab is missing so Join Team cannot soft-lock.
    /// </para>
    /// </summary>
    public partial class OrbitStationUI
    {
        /// <summary>
        /// Seconds after the first warmup tick before we let Join Team through even if
        /// some family grids are still missing. Show() still builds those as a fallback.
        /// </summary>
        const float JoinLoadWarmupTimeoutSeconds = 15f;

        /// <summary>Realtime when join warmup first ran this session. -1 = not started.</summary>
        static float s_JoinWarmupStartedRealtime = -1f;

        /// <summary>True after the timeout fired — loading must not wait forever.</summary>
        static bool s_JoinWarmupTimedOut;

        /// <summary>True after we logged a successful join warmup (avoid Console spam).</summary>
        static bool s_JoinWarmupCompleteLogged;

        /// <summary>
        /// [UNITY] After scene load — Game's loading screen cannot call this type (asmdef).
        /// We register tick / reset on <see cref="OrbitMenuJoinWarmupGate"/> instead.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RegisterJoinWarmupGate()
        {
            OrbitMenuJoinWarmupGate.TickHandler = TickJoinLoadWarmupAndPublish;
            OrbitMenuJoinWarmupGate.ResetHandler = ResetJoinWarmupSessionGateAndPublish;
            PublishJoinWarmupToGate();
        }

        /// <summary>
        /// True when the hidden tree exists and every catalog family has a GEAR grid.
        /// Loading / Join Team both wait on <see cref="IsJoinLoadWarmupCompleteOrNotNeeded"/>.
        /// </summary>
        public bool IsJoinLoadWarmupComplete =>
            _moonDockTreeWarmed && _moonDockReparentDone && AreAllCatalogFamilyStoresCached();

        /// <summary>
        /// Clears timeout / started-at flags on session leave so a later join can retry
        /// warmup. Does not destroy already-built widgets — a complete cache stays warm.
        /// </summary>
        public static void ResetJoinWarmupSessionGate()
        {
            s_JoinWarmupStartedRealtime = -1f;
            s_JoinWarmupTimedOut = false;
            s_JoinWarmupCompleteLogged = false;
        }

        /// <summary>
        /// Session-leave reset plus a republish so the Game gate matches UI flags.
        /// Also resets minimap warmup timeout.
        /// </summary>
        static void ResetJoinWarmupSessionGateAndPublish()
        {
            ResetJoinWarmupSessionGate();
            MinimapJoinWarmup.ResetSession();
            PublishJoinWarmupToGate();
        }

        /// <summary>One Orbit Menu step + one minimap step, then copy combined progress onto the Game gate.</summary>
        static void TickJoinLoadWarmupAndPublish()
        {
            // --- In-game: warmup already published complete — do not Find or build ---
            // [TITAN-ORBIT] NceGameFlowController used to Tick this every frame after spawn.
            // Each tick scene-scanned OrbitStationUI + MinimapController (~18 ms self time).
            if (OrbitMenuJoinWarmupGate.IsCompleteOrNotNeeded)
                return;

            TickJoinLoadWarmup();
            MinimapJoinWarmup.Tick();
            PublishJoinWarmupToGate();
        }

        /// <summary>
        /// Mirrors Orbit Menu + minimap warmup onto <see cref="OrbitMenuJoinWarmupGate"/> so
        /// Join Team and the loading bar do not need a TitanOrbit.UI assembly reference.
        /// </summary>
        static void PublishJoinWarmupToGate()
        {
            bool orbitReady = IsJoinLoadWarmupCompleteOrNotNeeded();
            bool minimapReady = MinimapJoinWarmup.IsCompleteOrNotNeeded();
            float progress = 0.5f * GetJoinLoadWarmupProgress() + 0.5f * MinimapJoinWarmup.GetProgress();
            string status;
            if (!orbitReady)
                status = GetJoinLoadWarmupStatusLabel();
            else if (!minimapReady)
                status = MinimapJoinWarmup.GetStatusLabel();
            else
                status = "Ready";

            OrbitMenuJoinWarmupGate.Publish(orbitReady && minimapReady, progress, status);
        }

        /// <summary>
        /// Join-presentation gate: dedicated servers and timed-out clients are treated as ready
        /// so the overlay cannot block the match. Otherwise the live UI must finish warmup.
        /// </summary>
        /// <returns>
        /// True when team select may appear. False while the loading screen should keep ticking
        /// <see cref="TickJoinLoadWarmup"/>.
        /// </returns>
        public static bool IsJoinLoadWarmupCompleteOrNotNeeded()
        {
            // --- Dedicated / headless: no Orbit Menu ---
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return true;

            if (s_JoinWarmupTimedOut)
                return true;

            OrbitStationUI ui = Instance;
            if (ui == null)
                return false;

            return ui.IsJoinLoadWarmupComplete;
        }

        /// <summary>
        /// 0–1 fill for the loading bar's menu slice. Shared chrome is the first half;
        /// catalog family grids are the second half.
        /// </summary>
        public static float GetJoinLoadWarmupProgress()
        {
            if (IsJoinLoadWarmupCompleteOrNotNeeded())
                return 1f;

            OrbitStationUI ui = Instance;
            if (ui == null)
                return 0f;

            int familyTotal = Mathf.Max(1, ui.CountDistinctCatalogFamilies());
            int sharedDone = (int)ui._moonDockWarmupPhase;
            if (sharedDone > 5)
                sharedDone = 5;

            int familyDone = 0;
            if (ui._moonDockWarmupPhase >= MoonDockWarmupPhase.Tree)
                familyDone = Mathf.Clamp(ui._familyStoreCache.Count, 0, familyTotal);

            float total = 5f + familyTotal;
            return Mathf.Clamp01((sharedDone + familyDone) / total);
        }

        /// <summary>In-bar status while menus are still Instantiating (honest counts).</summary>
        public static string GetJoinLoadWarmupStatusLabel()
        {
            if (IsJoinLoadWarmupCompleteOrNotNeeded())
                return "Orbit menus ready";

            OrbitStationUI ui = Instance;
            if (ui == null)
                return "Preparing orbit menus";

            if (ui._moonDockWarmupPhase < MoonDockWarmupPhase.Tree)
                return "Preparing orbit menus";

            int familyTotal = ui.CountDistinctCatalogFamilies();
            int familyDone = ui._familyStoreCache.Count;
            if (familyTotal > 0)
                return "Preparing orbit menus  " + familyDone + " / " + familyTotal;
            return "Preparing orbit menus";
        }

        /// <summary>
        /// Advances hidden Orbit Menu construction by one phase (or one family grid).
        /// Safe to call every frame from the loading screen and the game-flow controller —
        /// a frame stamp prevents double-advance when both tick.
        /// </summary>
        public static void TickJoinLoadWarmup()
        {
            // --- Client presentation only ---
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (s_JoinWarmupTimedOut)
                return;

            if (s_JoinWarmupStartedRealtime < 0f)
                s_JoinWarmupStartedRealtime = Time.realtimeSinceStartup;

            if (Time.realtimeSinceStartup - s_JoinWarmupStartedRealtime >= JoinLoadWarmupTimeoutSeconds)
            {
                s_JoinWarmupTimedOut = true;
                Debug.LogWarning(
                    "[OrbitMenu] Join-load warmup timed out after " +
                    JoinLoadWarmupTimeoutSeconds +
                    "s — Join Team will open; leftover grids build while flying.");
                return;
            }

            OrbitStationUI ui = GetOrCreate();
            if (ui._joinWarmupLastTickFrame == Time.frameCount)
                return;
            ui._joinWarmupLastTickFrame = Time.frameCount;

            if (ui.IsJoinLoadWarmupComplete)
            {
                LogJoinWarmupCompleteOnce(ui);
                return;
            }

            // Dummy ship is required: the local hull does not exist until after Join Team.
            ui.TickOrbitMenuWarmup(storePlanetHint: 0, homePlanetId: 0, allowDummyShip: true);

            if (ui.IsJoinLoadWarmupComplete)
                LogJoinWarmupCompleteOnce(ui);
        }

        /// <summary>One Console line when join warmup finishes so hitch hunts have a timestamp.</summary>
        static void LogJoinWarmupCompleteOnce(OrbitStationUI ui)
        {
            if (s_JoinWarmupCompleteLogged)
                return;
            s_JoinWarmupCompleteLogged = true;
            int families = ui != null ? ui._familyStoreCache.Count : 0;
            Debug.Log("[OrbitMenu] Join-load warmup complete (" + families + " family stores).");
        }

#if UNITY_EDITOR
        /// <summary>
        /// [UNITY] Domain Reload off leaves timeout / logged flags sticky across Play Mode.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetJoinWarmupStatics() => ResetJoinWarmupSessionGate();
#endif
    }
}
