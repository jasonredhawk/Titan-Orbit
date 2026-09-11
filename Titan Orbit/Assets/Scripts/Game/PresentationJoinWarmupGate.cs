using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Join-load graphics warmup progress that loading / Join Team can read from
    /// <c>TitanOrbit.Game</c> without touching UI assemblies.
    /// <para>
    /// First spawn used to 60↔30 VSync-bounce for ~30s because URP compiles shader
    /// variants on <b>first draw</b>, not on Instantiates. The loading overlay already
    /// absorbs map proxies, Orbit Menu chrome, and minimap blips. This gate holds Join
    /// Team until <see cref="PresentationJoinWarmup"/> has drawn cold materials, the
    /// starting hull, and budgeted VFX pool Instantiates under that overlay.
    /// </para>
    /// <para>
    /// Dedicated / headless servers have no client GPU work — <see cref="IsCompleteOrNotNeeded"/>
    /// is true immediately. Paired with <see cref="OrbitMenuJoinWarmupGate"/> (menus + minimap).
    /// </para>
    /// </summary>
    public static class PresentationJoinWarmupGate
    {
        /// <summary>Last complete flag published by the worker. False until the first client tick.</summary>
        static bool s_PublishedComplete;

        /// <summary>0–1 graphics slice for the loading bar.</summary>
        static float s_Progress;

        /// <summary>In-bar status, e.g. "Warming graphics  12 / 40".</summary>
        static string s_Status = "Warming graphics";

        /// <summary>
        /// True when Join Team may appear: dedicated / headless skip, or the worker finished
        /// (or timed out) hidden shader / hull / VFX warmup.
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

        /// <summary>0–1 fill for the loading bar's graphics slice.</summary>
        public static float Progress =>
            IsCompleteOrNotNeeded ? 1f : Mathf.Clamp01(s_Progress);

        /// <summary>Loading-bar caption while materials and VFX Instantiates under the overlay.</summary>
        public static string StatusLabel =>
            string.IsNullOrEmpty(s_Status) ? "Warming graphics" : s_Status;

        /// <summary>
        /// Called by <see cref="PresentationJoinWarmup"/> after each tick so the loading bar
        /// and Join Team gate stay in sync.
        /// </summary>
        /// <param name="completeOrNotNeeded">True when warmup finished, timed out, or is not required.</param>
        /// <param name="progress">0–1 construction progress.</param>
        /// <param name="status">Short honest status line.</param>
        public static void Publish(bool completeOrNotNeeded, float progress, string status)
        {
            s_PublishedComplete = completeOrNotNeeded;
            s_Progress = Mathf.Clamp01(progress);
            s_Status = status ?? "Warming graphics";
        }

        /// <summary>
        /// Advances hidden graphics warmup by one budgeted slice. No-ops on dedicated server.
        /// LoadingScreen and <c>NceGameFlowController</c> both call this — the worker
        /// de-dupes with <c>Time.frameCount</c>.
        /// </summary>
        public static void Tick()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            PresentationJoinWarmup.Tick();
        }

        /// <summary>
        /// Session leave / main menu: tear down the warmup camera and reset flags so the
        /// next join can retry. GPU programs stay resident in-process (second join is cheap).
        /// </summary>
        public static void ResetSession()
        {
            PresentationJoinWarmup.ResetSession();
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                s_PublishedComplete = true;
                s_Progress = 1f;
                s_Status = "Graphics ready";
                return;
            }

            s_PublishedComplete = false;
            s_Progress = 0f;
            s_Status = "Warming graphics";
        }

#if UNITY_EDITOR
        /// <summary>[UNITY] Domain Reload off leaves published flags sticky across Play Mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_PublishedComplete = false;
            s_Progress = 0f;
            s_Status = "Warming graphics";
        }
#endif
    }
}
