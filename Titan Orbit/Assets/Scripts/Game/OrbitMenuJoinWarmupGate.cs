using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Join-load presentation progress that <c>TitanOrbit.Game</c> can read without referencing
    /// the default UI assembly (Assembly-CSharp). <c>OrbitStationUI</c> registers tick / reset
    /// handlers after scene load and publishes complete / progress / status each tick
    /// (Orbit Menu chrome + family stores <b>and</b> minimap blips).
    /// <para>
    /// Loading screen and Join Team wait on <see cref="IsCompleteOrNotNeeded"/> so first spawn
    /// does not Instantiates those widgets. Dedicated servers have no client HUD — this
    /// gate reports complete immediately.
    /// </para>
    /// </summary>
    public static class OrbitMenuJoinWarmupGate
    {
        /// <summary>
        /// UI-side one-phase warmup step. Null until <c>OrbitStationUI</c> registers
        /// (or on a dedicated server that never loads that type).
        /// </summary>
        public static System.Action TickHandler { get; set; }

        /// <summary>UI-side session-leave reset (timeout flags only — built widgets stay).</summary>
        public static System.Action ResetHandler { get; set; }

        /// <summary>Last complete flag published by UI. False until the first publish on a client.</summary>
        static bool s_PublishedComplete;

        /// <summary>0–1 menu slice for the loading bar.</summary>
        static float s_Progress;

        /// <summary>In-bar status, e.g. "Preparing orbit menus  3 / 12" or minimap counts.</summary>
        static string s_Status = "Preparing menus";

        /// <summary>
        /// True when Join Team may appear: dedicated / headless skip, or UI finished
        /// (or timed out) hidden Orbit Menu construction.
        /// </summary>
        public static bool IsCompleteOrNotNeeded
        {
            get
            {
                if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                    return true;
                return s_PublishedComplete;
            }
        }

        /// <summary>0–1 fill for the loading bar's Orbit Menu slice.</summary>
        public static float Progress =>
            IsCompleteOrNotNeeded ? 1f : Mathf.Clamp01(s_Progress);

        /// <summary>Loading-bar caption while menus Instantiates off-screen.</summary>
        public static string StatusLabel =>
            string.IsNullOrEmpty(s_Status) ? "Preparing orbit menus" : s_Status;

        /// <summary>
        /// Called by <c>OrbitStationUI</c> after each warmup tick so Game can paint the bar
        /// and release Join Team without a UI assembly reference.
        /// </summary>
        /// <param name="completeOrNotNeeded">True when warmup finished, timed out, or is not required.</param>
        /// <param name="progress">0–1 construction progress.</param>
        /// <param name="status">Short honest status line.</param>
        public static void Publish(bool completeOrNotNeeded, float progress, string status)
        {
            s_PublishedComplete = completeOrNotNeeded;
            s_Progress = Mathf.Clamp01(progress);
            s_Status = status ?? "Preparing orbit menus";
        }

        /// <summary>
        /// Advances hidden Orbit Menu construction by one phase when a UI handler is registered.
        /// No-ops on dedicated server.
        /// </summary>
        public static void Tick()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            TickHandler?.Invoke();
        }

        /// <summary>
        /// Session leave / main menu: ask UI to clear timeout flags so the next join can retry.
        /// Already-built widgets stay warm.
        /// </summary>
        public static void ResetSession()
        {
            ResetHandler?.Invoke();
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                s_PublishedComplete = true;
                s_Progress = 1f;
                s_Status = "Orbit menus ready";
            }
        }

#if UNITY_EDITOR
        /// <summary>[UNITY] Domain Reload off leaves published flags sticky across Play Mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            TickHandler = null;
            ResetHandler = null;
            s_PublishedComplete = false;
            s_Progress = 0f;
            s_Status = "Preparing orbit menus";
        }
#endif
    }
}
