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
        /// Extra compose slots unlocked this match (0–2). One rewarded ad each.
        /// Default sentence is 3 words; two ads unlock the 4th and 5th.
        /// </summary>
        static int s_ExtraKeywordSlots;

        /// <summary>True when the player clicked the docked minimap this hold.</summary>
        static bool s_HasPendingWaypoint;

        /// <summary>True until the compose panel inserts a Here chip for the latest ping.</summary>
        static bool s_WaypointChipDirty;

        /// <summary>World XZ of the pending minimap ping (Y unused).</summary>
        static Vector3 s_PendingWaypoint;

        /// <summary>True when the pointer last unprojected onto the play plane off the HUD.</summary>
        static bool s_HasLastPlayAim;

        /// <summary>Last play-plane aim while the pointer was not over UI.</summary>
        static Vector3 s_LastPlayAim;

        /// <summary>True when this hold locked a "You" ship from the mouse.</summary>
        static bool s_HasPendingYou;

        /// <summary>NetworkId of the closest ship to the pointer when "You" was clicked.</summary>
        static int s_PendingYouNetworkId;

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

        /// <summary>0–2 extra slots unlocked this match via rewarded ads.</summary>
        public static int ExtraKeywordSlots => s_ExtraKeywordSlots;

        /// <summary>
        /// How many keywords the local player may compose right now (3–5).
        /// </summary>
        public static int AllowedSequenceLength =>
            3 + Mathf.Clamp(s_ExtraKeywordSlots, 0, 2);

        /// <summary>True when the current hold has a minimap ping ready to send.</summary>
        public static bool HasPendingWaypoint => s_HasPendingWaypoint;

        /// <summary>World position of the pending minimap ping.</summary>
        public static Vector3 PendingWaypoint => s_PendingWaypoint;

        /// <summary>True when a play-plane aim sample exists for this session.</summary>
        public static bool HasLastPlayAim => s_HasLastPlayAim;

        /// <summary>Last world aim taken while the pointer was off the compose HUD.</summary>
        public static Vector3 LastPlayAim => s_LastPlayAim;

        /// <summary>True when this hold locked a "You" ship.</summary>
        public static bool HasPendingYou => s_HasPendingYou;

        /// <summary>NetworkId locked by clicking "You". 0 when none.</summary>
        public static int PendingYouNetworkId => s_PendingYouNetworkId;

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
            s_ExtraKeywordSlots = 0;
            s_HasPendingWaypoint = false;
            s_PendingWaypoint = Vector3.zero;
            s_WaypointChipDirty = false;
            s_HasLastPlayAim = false;
            s_LastPlayAim = Vector3.zero;
            s_HasPendingYou = false;
            s_PendingYouNetworkId = 0;
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
        /// Sets how many extra keyword slots this match has unlocked (0–2).
        /// One rewarded ad per slot; Orbit Unlocked grants both at once.
        /// </summary>
        public static void SetExtraKeywordSlots(int extraSlots)
        {
            s_ExtraKeywordSlots = Mathf.Clamp(extraSlots, 0, 2);
        }

        /// <summary>Stores a world ping from the docked comms minimap for the next send.</summary>
        public static void SetPendingWaypoint(Vector3 worldPos)
        {
            s_HasPendingWaypoint = true;
            s_PendingWaypoint = worldPos;
            s_WaypointChipDirty = true;
        }

        /// <summary>True when the compose panel should insert or refresh a Here chip.</summary>
        public static bool ConsumeWaypointChipDirty()
        {
            if (!s_WaypointChipDirty)
                return false;
            s_WaypointChipDirty = false;
            return true;
        }

        /// <summary>Drops the pending ping (new hold, send consumed, or cancel).</summary>
        public static void ClearPendingWaypoint()
        {
            s_HasPendingWaypoint = false;
            s_PendingWaypoint = Vector3.zero;
            s_WaypointChipDirty = false;
        }

        /// <summary>
        /// Stores a play-plane aim sampled while the pointer was not over the HUD.
        /// "You" / planet / asteroid resolve from this so a click on the tile does not
        /// unproject through the compose card.
        /// </summary>
        public static void SetLastPlayAim(Vector3 worldPos)
        {
            s_HasLastPlayAim = true;
            s_LastPlayAim = worldPos;
        }

        /// <summary>Locks the closest-in-range ship for the next send's "You".</summary>
        public static void SetPendingYou(int networkId)
        {
            s_HasPendingYou = networkId > 0;
            s_PendingYouNetworkId = networkId > 0 ? networkId : 0;
        }

        /// <summary>Drops the pending "You" lock (new hold, toggle-off, or send consumed).</summary>
        public static void ClearPendingYou()
        {
            s_HasPendingYou = false;
            s_PendingYouNetworkId = 0;
        }

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
