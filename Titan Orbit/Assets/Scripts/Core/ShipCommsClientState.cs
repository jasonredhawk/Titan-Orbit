using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only state for the hold-S comms matrix. <c>ShipCommsPanel</c> writes
    /// <see cref="IsOpen"/> while the overlay is visible; input and cursor code read it so
    /// left-click selects keywords instead of firing the gun. <see cref="TeamOnly"/> is the
    /// remembered All / Team channel (PlayerPrefs) the panel and send path share.
    /// <para>
    /// Lives in Core (not the UI assembly) so <c>ShipInputBridge</c> and
    /// <c>GameplayCursorController</c> can see it without a Game → Assembly-CSharp reference.
    /// Not replicated — the server never needs to know the panel is open; it only sees the
    /// <c>TeamOnly</c> bit on the RPC and re-checks the speaker's team itself.
    /// </para>
    /// </summary>
    public static class ShipCommsClientState
    {
        /// <summary>
        /// [UNITY] PlayerPrefs key for the All / Team channel. Same machine remembers
        /// the last choice; a missing key means All (the original broadcast).
        /// </summary>
        public const string TeamOnlyPrefsKey = "TitanOrbit.ShipComms.TeamOnly";

        /// <summary>
        /// True while the local player is holding S and the compose card is on screen.
        /// </summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// True when the next send should go to teammates only. Loaded once from
        /// PlayerPrefs; <see cref="SetTeamOnly"/> writes it back.
        /// </summary>
        static bool s_TeamOnly;

        /// <summary>False until the first <see cref="TeamOnly"/> read this process.</summary>
        static bool s_TeamOnlyLoaded;

        /// <summary>
        /// Active PlayerPrefs key. The compose panel may swap this for an MPPM
        /// instance suffix so Player 2 does not overwrite Player 1's channel.
        /// </summary>
        static string s_PrefsKey = TeamOnlyPrefsKey;

        /// <summary>
        /// Current All / Team channel. All (false) is the default for first-time players.
        /// </summary>
        public static bool TeamOnly
        {
            get
            {
                EnsureTeamOnlyLoaded();
                return s_TeamOnly;
            }
        }

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky across Play Mode. Clear the
        /// fire-suppression flag and drop the prefs cache so the next Play re-reads disk.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            IsOpen = false;
            s_TeamOnlyLoaded = false;
            s_TeamOnly = false;
            s_PrefsKey = TeamOnlyPrefsKey;
        }

        /// <summary>
        /// Points later reads/writes at <paramref name="prefsKey"/> (MPPM per-instance
        /// keys). Call once at panel Awake before the first <see cref="TeamOnly"/> get.
        /// </summary>
        /// <param name="prefsKey">PlayerPrefs key for this Editor / player instance.</param>
        public static void BindPrefsKey(string prefsKey)
        {
            if (string.IsNullOrEmpty(prefsKey))
                return;

            s_PrefsKey = prefsKey;
            s_TeamOnlyLoaded = false;
        }

        /// <summary>
        /// Called from <c>ShipCommsPanel</c> when the matrix shows or hides.
        /// </summary>
        /// <param name="open">True while S is held and the card is interactable.</param>
        public static void SetOpen(bool open) => IsOpen = open;

        /// <summary>
        /// Flips or sets the All / Team channel and writes PlayerPrefs so the next
        /// session (and the next hold-S) keep the same choice.
        /// </summary>
        /// <param name="teamOnly">True = teammates only; false = every client.</param>
        public static void SetTeamOnly(bool teamOnly)
        {
            // --- Persist ---
            // [UNITY] PlayerPrefs is a tiny local key/value store (registry on Windows,
            // plist on macOS). Not a server setting — each machine remembers its own toggle.
            s_TeamOnlyLoaded = true;
            s_TeamOnly = teamOnly;
            PlayerPrefs.SetInt(s_PrefsKey, teamOnly ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Reads the saved channel once per process. Missing key → All, matching the
        /// original "broadcast to every client" behavior.
        /// </summary>
        static void EnsureTeamOnlyLoaded()
        {
            if (s_TeamOnlyLoaded)
                return;

            s_TeamOnlyLoaded = true;
            s_TeamOnly = PlayerPrefs.GetInt(s_PrefsKey, 0) != 0;
        }
    }
}
