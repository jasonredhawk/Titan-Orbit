using TitanOrbit.Game;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Join-load minimap warmup. The loading overlay ticks this so planet / moon / asteroid
    /// blips Instantiates <b>before</b> Join Team. First spawn used to hitch because the HUD
    /// stayed inactive until the ship appeared, then <see cref="MinimapController"/> created
    /// every blip on the first visible frame.
    /// <para>
    /// Dedicated servers skip this. A timeout still releases Join Team if the HUD prefab is
    /// missing so loading cannot soft-lock.
    /// </para>
    /// </summary>
    public static class MinimapJoinWarmup
    {
        /// <summary>Seconds after the first tick before we let Join Team through anyway.</summary>
        const float TimeoutSeconds = 15f;

        /// <summary>
        /// Seconds to wait for a <see cref="MinimapController"/> to exist. No HUD in the
        /// scene means warmup is not needed.
        /// </summary>
        const float MissingControllerGraceSeconds = 3f;

        /// <summary>Realtime when warmup first ran this session. -1 = not started.</summary>
        static float s_StartedRealtime = -1f;

        /// <summary>True after timeout / missing-HUD grace — do not block Join Team.</summary>
        static bool s_TimedOut;

        /// <summary>
        /// Join-presentation gate piece: dedicated / timed-out / missing HUD are ready.
        /// Otherwise the live minimap must finish hidden blip construction.
        /// </summary>
        public static bool IsCompleteOrNotNeeded()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return true;
            if (s_TimedOut)
                return true;

            MinimapController minimap = FindMinimap();
            if (minimap == null)
                return false;
            return minimap.IsJoinLoadWarmupComplete;
        }

        /// <summary>0–1 fill for the loading bar's minimap slice.</summary>
        public static float GetProgress()
        {
            if (IsCompleteOrNotNeeded())
                return 1f;
            MinimapController minimap = FindMinimap();
            return minimap != null ? minimap.GetJoinLoadWarmupProgress() : 0f;
        }

        /// <summary>In-bar status while minimap blips Instantiates off-screen.</summary>
        public static string GetStatusLabel()
        {
            if (IsCompleteOrNotNeeded())
                return "Minimap ready";
            MinimapController minimap = FindMinimap();
            return minimap != null
                ? minimap.GetJoinLoadWarmupStatusLabel()
                : "Preparing minimap";
        }

        /// <summary>
        /// One warmup step: wake the HUD hierarchy (hidden), upsert map-body anchors,
        /// then Instantiates a budgeted batch of blips.
        /// </summary>
        public static void Tick()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (s_TimedOut)
                return;
            // Already finished — do not wake the HUD or force CanvasGroup alpha 0 after spawn.
            if (IsCompleteOrNotNeeded())
                return;

            if (s_StartedRealtime < 0f)
                s_StartedRealtime = Time.realtimeSinceStartup;

            float elapsed = Time.realtimeSinceStartup - s_StartedRealtime;
            MinimapController minimap = FindMinimap();
            if (minimap == null)
            {
                if (elapsed >= MissingControllerGraceSeconds)
                    s_TimedOut = true;
                return;
            }

            if (elapsed >= TimeoutSeconds)
            {
                s_TimedOut = true;
                Debug.LogWarning(
                    "[Minimap] Join-load warmup timed out after " + TimeoutSeconds +
                    "s — Join Team will open; leftover blips build on first show.");
                return;
            }

            minimap.TickJoinLoadWarmup();
        }

        /// <summary>
        /// Session leave: clear timeout so the next join can retry. Built blips stay on
        /// the minimap object.
        /// </summary>
        public static void ResetSession()
        {
            s_StartedRealtime = -1f;
            s_TimedOut = false;
        }

        /// <summary>Finds the scene minimap even while the HUD root is inactive.</summary>
        static MinimapController FindMinimap()
        {
            return Object.FindFirstObjectByType<MinimapController>(FindObjectsInactive.Include);
        }

#if UNITY_EDITOR
        /// <summary>[UNITY] Domain Reload off leaves timeout sticky across Play Mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ResetSession();
#endif
    }
}
