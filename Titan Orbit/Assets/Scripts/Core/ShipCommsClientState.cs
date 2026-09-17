using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only state for the hold-S comms matrix. <c>ShipCommsPanel</c> writes
    /// <see cref="IsOpen"/> while the overlay is visible; input and cursor code read it so
    /// left-click selects keywords instead of firing the gun. <see cref="Channel"/> is the
    /// remembered All / Team / Commander audience (PlayerPrefs) the panel and send path share.
    /// <para>
    /// Lives in Core (not the UI assembly) so <c>ShipInputBridge</c> and
    /// <c>GameplayCursorController</c> can see it without a Game → Assembly-CSharp reference.
    /// Not replicated — the server never needs to know the panel is open; it only sees the
    /// channel byte on the RPC and re-checks the speaker's team and commander rank itself.
    /// </para>
    /// </summary>
    public static class ShipCommsClientState
    {
        /// <summary>
        /// [UNITY] PlayerPrefs key for the All / Team / Commander channel. Same machine
        /// remembers the last choice; a missing key means All (the original broadcast).
        /// Older builds stored 0/1 only — 2 is Commander.
        /// </summary>
        public const string TeamOnlyPrefsKey = "TitanOrbit.ShipComms.TeamOnly";

        /// <summary>
        /// True while the local player is holding S and the compose card is on screen.
        /// </summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// True when the next send should go to teammates only (Team or Commander).
        /// Loaded once from PlayerPrefs; <see cref="SetChannel"/> writes it back.
        /// </summary>
        public static bool TeamOnly => Channel != ShipCommsChannel.All;

        /// <summary>
        /// Current All / Team / Commander audience. All is the default for first-time players.
        /// Commander is only valid while this machine is a top-three commander — the panel
        /// drops back to Team when rank falls.
        /// </summary>
        public static ShipCommsChannel Channel
        {
            get
            {
                EnsureChannelLoaded();
                return s_Channel;
            }
        }

        /// <summary>Remembered All / Team / Commander byte.</summary>
        static ShipCommsChannel s_Channel;

        /// <summary>False until the first <see cref="Channel"/> read this process.</summary>
        static bool s_ChannelLoaded;

        /// <summary>
        /// Active PlayerPrefs key. The compose panel may swap this for an MPPM
        /// instance suffix so Player 2 does not overwrite Player 1's channel.
        /// </summary>
        static string s_PrefsKey = TeamOnlyPrefsKey;

        /// <summary>
        /// Extra compose slots unlocked this match (0 or 2). One rewarded ad
        /// unlocks both the 4th and 5th words. Default sentence is 3 words.
        /// </summary>
        static int s_ExtraKeywordSlots;

        /// <summary>
        /// True when one rewarded ad (or Orbit Unlocked) opened every RECENT row
        /// past the free first three. Same match lifetime as the extra keyword slots.
        /// </summary>
        static bool s_RecentRowsUnlocked;

        /// <summary>True when the player clicked the docked minimap this hold.</summary>
        static bool s_HasPendingWaypoint;

        /// <summary>True until the compose panel inserts a Here chip for the latest ping.</summary>
        static bool s_WaypointChipDirty;

        /// <summary>World XZ of the pending minimap ping (Y unused).</summary>
        static Vector3 s_PendingWaypoint;

        /// <summary>PlanetId when the ping landed on a planet disc. 0 for a bare Here point.</summary>
        static int s_PendingPlanetId;

        /// <summary>True when <see cref="s_PendingPlanetId"/> is a team home world.</summary>
        static bool s_PendingPlanetIsHome;

        /// <summary>Ghosted family index for a designer planetName override. −1 = infer.</summary>
        static int s_PendingPlanetFamilyIndex;

        /// <summary>True when the pointer last unprojected onto the play plane off the HUD.</summary>
        static bool s_HasLastPlayAim;

        /// <summary>Last play-plane aim while the pointer was not over UI.</summary>
        static Vector3 s_LastPlayAim;

        /// <summary>True when this hold locked a "You" ship from the mouse.</summary>
        static bool s_HasPendingYou;

        /// <summary>NetworkId of the closest ship to the pointer when "You" was clicked.</summary>
        static int s_PendingYouNetworkId;

        /// <summary>
        /// True when this hold has "Us" on the rail. Compose rings the speaker plus
        /// friendlies inside <c>YouSelectRange</c> of that hull — not the mouse.
        /// </summary>
        static bool s_HasPendingUs;

        /// <summary>
        /// Free RECENT rows for players who have not watched the unlock ad.
        /// Rows after this sit under one unlock plate until that video (or remove-ads) opens them all.
        /// </summary>
        public const int FreeRecentRows = 3;

        /// <summary>0 or 2 extra keyword slots unlocked this match via one rewarded ad.</summary>
        public static int ExtraKeywordSlots => s_ExtraKeywordSlots;

        /// <summary>
        /// How many keywords the local player may compose right now (3–5).
        /// </summary>
        public static int AllowedSequenceLength =>
            3 + Mathf.Clamp(s_ExtraKeywordSlots, 0, 2);

        /// <summary>
        /// True when every RECENT row past <see cref="FreeRecentRows"/> is usable.
        /// </summary>
        public static bool RecentRowsUnlocked => s_RecentRowsUnlocked;

        /// <summary>True when the current hold has a minimap ping ready to send.</summary>
        public static bool HasPendingWaypoint => s_HasPendingWaypoint;

        /// <summary>World position of the pending minimap ping.</summary>
        public static Vector3 PendingWaypoint => s_PendingWaypoint;

        /// <summary>True when the pending Here ping is a named planet disc.</summary>
        public static bool HasPendingPlanet => s_HasPendingWaypoint && s_PendingPlanetId > 0;

        /// <summary>PlanetId locked by clicking a comms-map planet. 0 when the ping is a bare point.</summary>
        public static int PendingPlanetId => s_PendingPlanetId;

        /// <summary>True when the locked planet is a team home world.</summary>
        public static bool PendingPlanetIsHome => s_PendingPlanetIsHome;

        /// <summary>Family index for the locked planet's optional designer name. −1 = infer.</summary>
        public static int PendingPlanetFamilyIndex => s_PendingPlanetFamilyIndex;

        /// <summary>True when a play-plane aim sample exists for this session.</summary>
        public static bool HasLastPlayAim => s_HasLastPlayAim;

        /// <summary>Last world aim taken while the pointer was off the compose HUD.</summary>
        public static Vector3 LastPlayAim => s_LastPlayAim;

        /// <summary>True when this hold locked a "You" ship.</summary>
        public static bool HasPendingYou => s_HasPendingYou;

        /// <summary>NetworkId locked by clicking "You". 0 when none.</summary>
        public static int PendingYouNetworkId => s_PendingYouNetworkId;

        /// <summary>True when this hold has "Us" on the compose rail.</summary>
        public static bool HasPendingUs => s_HasPendingUs;

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky across Play Mode. Clear the
        /// fire-suppression flag and drop the prefs cache so the next Play re-reads disk.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            IsOpen = false;
            s_ChannelLoaded = false;
            s_Channel = ShipCommsChannel.All;
            s_PrefsKey = TeamOnlyPrefsKey;
            s_ExtraKeywordSlots = 0;
            s_RecentRowsUnlocked = false;
            s_HasPendingWaypoint = false;
            s_PendingWaypoint = Vector3.zero;
            s_WaypointChipDirty = false;
            s_PendingPlanetId = 0;
            s_PendingPlanetIsHome = false;
            s_PendingPlanetFamilyIndex = -1;
            s_HasLastPlayAim = false;
            s_LastPlayAim = Vector3.zero;
            s_HasPendingYou = false;
            s_PendingYouNetworkId = 0;
            s_HasPendingUs = false;
        }

        /// <summary>
        /// Points later reads/writes at <paramref name="prefsKey"/> (MPPM per-instance
        /// keys). Call once at panel Awake before the first <see cref="Channel"/> get.
        /// </summary>
        /// <param name="prefsKey">PlayerPrefs key for this Editor / player instance.</param>
        public static void BindPrefsKey(string prefsKey)
        {
            if (string.IsNullOrEmpty(prefsKey))
                return;

            s_PrefsKey = prefsKey;
            s_ChannelLoaded = false;
        }

        /// <summary>
        /// Called from <c>ShipCommsPanel</c> when the matrix shows or hides.
        /// </summary>
        /// <param name="open">True while S is held and the card is interactable.</param>
        public static void SetOpen(bool open) => IsOpen = open;

        /// <summary>
        /// Sets how many extra keyword slots this match has unlocked (0 or 2).
        /// One rewarded ad grants both; Orbit Unlocked also grants both at once.
        /// </summary>
        public static void SetExtraKeywordSlots(int extraSlots)
        {
            s_ExtraKeywordSlots = extraSlots >= 2 ? 2 : 0;
        }

        /// <summary>
        /// Opens or closes the paid RECENT rows for this match. One ad sets
        /// <paramref name="unlocked"/> true and every row past the free three works.
        /// </summary>
        /// <param name="unlocked">True after a completed rewarded ad or remove-ads.</param>
        public static void SetRecentRowsUnlocked(bool unlocked)
        {
            s_RecentRowsUnlocked = unlocked;
        }

        /// <summary>
        /// True when this RECENT index is free or the unlock ad already ran.
        /// Index 0 is the newest sentence.
        /// </summary>
        /// <param name="index">Row in the RECENT column (0 = newest).</param>
        public static bool IsRecentRowUnlocked(int index)
        {
            if (index < 0)
                return false;
            return s_RecentRowsUnlocked || index < FreeRecentRows;
        }

        /// <summary>
        /// Stores a world ping from the docked comms minimap for the next send.
        /// A bare point (empty space) clears any previous planet name.
        /// </summary>
        public static void SetPendingWaypoint(Vector3 worldPos)
        {
            SetPendingWaypoint(worldPos, planetId: 0, isHomePlanet: false, familyIndex: -1);
        }

        /// <summary>
        /// Stores a Here ping. When <paramref name="planetId"/> is positive the compose
        /// chip reads that world's name instead of HERE.
        /// </summary>
        public static void SetPendingWaypoint(
            Vector3 worldPos, int planetId, bool isHomePlanet, int familyIndex)
        {
            s_HasPendingWaypoint = true;
            s_PendingWaypoint = worldPos;
            s_WaypointChipDirty = true;
            s_PendingPlanetId = planetId > 0 ? planetId : 0;
            s_PendingPlanetIsHome = planetId > 0 && isHomePlanet;
            s_PendingPlanetFamilyIndex = planetId > 0 ? familyIndex : -1;
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
            s_PendingPlanetId = 0;
            s_PendingPlanetIsHome = false;
            s_PendingPlanetFamilyIndex = -1;
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
        /// Marks "Us" as live for this hold so compose can ring the speaker plus
        /// nearby friendlies. The set is gathered live each draw — we do not freeze
        /// ids here the way You does, because ships can enter or leave that circle
        /// while S is still held.
        /// </summary>
        public static void SetPendingUs()
        {
            s_HasPendingUs = true;
        }

        /// <summary>Drops the pending "Us" preview (new hold, toggle-off, or send consumed).</summary>
        public static void ClearPendingUs()
        {
            s_HasPendingUs = false;
        }

        /// <summary>
        /// Forgets the last play-plane aim so the next send resolves from the speaker hull.
        /// Recent-sentence reuse must not keep the previous rock / planet click.
        /// </summary>
        public static void ClearLastPlayAim()
        {
            s_HasLastPlayAim = false;
            s_LastPlayAim = Vector3.zero;
        }

        /// <summary>
        /// Legacy All / Team writer. Commander is a third value — use
        /// <see cref="SetChannel"/> when the compose panel has three pills.
        /// </summary>
        /// <param name="teamOnly">True = teammates; false = every client.</param>
        public static void SetTeamOnly(bool teamOnly)
        {
            SetChannel(teamOnly ? ShipCommsChannel.Team : ShipCommsChannel.All);
        }

        /// <summary>
        /// Flips or sets the All / Team / Commander channel and writes PlayerPrefs so the
        /// next session (and the next hold-S) keep the same choice.
        /// </summary>
        /// <param name="channel">Audience the next send should request.</param>
        public static void SetChannel(ShipCommsChannel channel)
        {
            // --- Persist ---
            // [UNITY] PlayerPrefs is a tiny local key/value store (registry on Windows,
            // plist on macOS). Not a server setting — each machine remembers its own toggle.
            // The int is the enum byte (0 All, 1 Team, 2 Commander).
            s_ChannelLoaded = true;
            s_Channel = TeamCommanderRules.Sanitize((byte)channel);
            PlayerPrefs.SetInt(s_PrefsKey, (int)s_Channel);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Reads the saved channel once per process. Missing key → All, matching the
        /// original "broadcast to every client" behavior.
        /// </summary>
        static void EnsureChannelLoaded()
        {
            if (s_ChannelLoaded)
                return;

            s_ChannelLoaded = true;
            s_Channel = TeamCommanderRules.Sanitize((byte)PlayerPrefs.GetInt(s_PrefsKey, 0));
        }
    }
}
