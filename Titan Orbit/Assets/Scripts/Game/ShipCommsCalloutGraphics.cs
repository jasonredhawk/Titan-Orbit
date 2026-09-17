using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Resolves hold-S keyword sentences into world waypoints and paints a simple
    /// on/off line between those targets. Sender locks identities (You / Us /
    /// planet id) onto the RPC so every viewer follows the same movers.
    /// <para>
        /// Word order is the path direction: "Me Planet" is Me → planet; "Planet Me"
        /// the other way. Consecutive nouns each get a segment ("Me Asteroid Moon"
        /// is Me → asteroid and asteroid → moon). A lone world noun implies Me,
        /// unless the sentence named You / Us and nobody was locked — then the
        /// source stays empty and only the world ring draws ("You Asteroid" is a
        /// rock circle). Us is friendlies inside <see cref="YouSelectRange"/> of
        /// the speaker hull. Escort is every teammate with troops aboard inside
        /// that range of the mouse aim. Team-owned nouns default to the speaker's
        /// team unless Attack / Enemy / a color word says otherwise. Verbs tint
        /// the following segment.
        /// Group words (Us / Escort / Everyone) fan to the next node.
    /// Lines grow from source to dest and repeat so travel direction is obvious.
    /// Endpoints are hollow rings sized to each target's collider.
        /// Top-3 team rank thickens the stroke only — no medal lining.
    /// Commander-keyword paths linger 10s after the 4s chips fade, thinner and quieter.
    /// Target rings expire with the chips — only the stroke stays.
    /// Map size from <see cref="ToroidalMap"/>.
    /// </para>
    /// Client presentation only — no ECS gathers.
    /// </summary>
    public static class ShipCommsCalloutGraphics
    {
        public const float YouSelectRange = 120f;
        /// <summary>Wire slots <c>Us0</c>–<c>Us3</c>. Identity is locked; pose is live.</summary>
        public const int MaxUsLocks = 4;
        public const int MaxEveryone = 8;
        /// <summary>
        /// How many tagged hulls may receive a commander echo chip (Everyone / Us / You).
        /// Larger than <see cref="MaxEveryone"/> line seats so a full squad still sees the order.
        /// </summary>
        public const int MaxTaggedPlayers = 16;

        /// <summary>
        /// After the chip row fades, commander-keyword paths stay this many more seconds
        /// as a thin ghost so the order remains readable without covering the fight.
        /// </summary>
        public const float CommanderPathLingerSeconds = 10f;

        /// <summary>Full-opacity path core while the 4s chips are still up.</summary>
        public const float PathLineLiveAlpha = 0.95f;

        /// <summary>
        /// Ghost-path opacity after the chips fade. Low enough that hulls and beams
        /// stay readable; high enough that a teammate can still follow the order.
        /// </summary>
        public const float PathLineLingerAlpha = 0.38f;

        /// <summary>
        /// Rank stroke shrinks to this fraction during linger (rank-1 6px → ~2.3px).
        /// Floored by <see cref="PathLineLingerMinPixels"/> so it cannot vanish.
        /// </summary>
        public const float PathLineLingerStrokeScale = 0.38f;

        /// <summary>Smallest linger core in screen pixels.</summary>
        public const float PathLineLingerMinPixels = 1.15f;

        /// <summary>Line grows from source to dest, then repeats so direction is readable.</summary>
        const float PathTravelOnSeconds = 0.55f;
        const float PathTravelGapSeconds = 0.12f;
        const float PathLineThinPixels = 2f;
        const float PathLineRank3Pixels = 3.4f;
        const float PathLineRank2Pixels = 4.6f;
        const float PathLineRank1Pixels = 6f;
        const float PathLineMinimapScale = 0.92f;
        /// <summary>Last slice of the linger window fades the ghost instead of popping off.</summary>
        const float PathLingerFadeSeconds = 0.65f;

        const int MaxPaths = 8;
        const float NodeRadius = 0.34f;
        const float PlayPlaneY = 0.14f;
        const float DefaultPingRadius = 0.38f;
        /// <summary>
        /// Tight halo just outside the collider. Thickness is pixels so the ring
        /// stays readable from the gameplay camera.
        /// </summary>
        const float RingSmallScale = 1.28f;
        const float RingSmallPad = 0.38f;
        const float RingSmallMax = 5f;
        const float RingLargeScale = 1.08f;
        const float RingLargePad = 0.32f;
        const float RingMinRadius = 0.62f;
        const float RingThicknessPixels = 2.8f;

        static readonly int[] s_IdScratch = new int[MaxEveryone];
        static readonly int[] s_AnchorScratch = new int[8];
        static readonly int[] s_TagIdScratch = new int[MaxTaggedPlayers];
        static readonly Vector3[] s_TagPosScratch = new Vector3[MaxTaggedPlayers];
        static readonly Vector3[] s_PosScratch = new Vector3[MaxEveryone];
        static readonly Vector3[] s_ExpandA = new Vector3[MaxEveryone];
        static readonly Vector3[] s_ExpandB = new Vector3[MaxEveryone];
        static readonly Vector3[] s_PathFrom = new Vector3[MaxPaths];
        static readonly Vector3[] s_PathTo = new Vector3[MaxPaths];
        static readonly float[] s_PathFromR = new float[MaxPaths];
        static readonly float[] s_PathToR = new float[MaxPaths];
        static readonly Color[] s_PathColor = new Color[MaxPaths];
        static readonly Color[] s_AnchorInbound = new Color[8];
        static readonly float[] s_RadiusA = new float[MaxEveryone];
        static readonly float[] s_RadiusB = new float[MaxEveryone];
        static int s_LastNodeCount;
        static int s_ExtraRockKey;
        static Vector3 s_ExtraRock;
        static float s_ExtraRockR;
        static bool s_ExtraRockOk;

        /// <summary>
        /// Fills sender-resolved locks on <paramref name="callout"/> from the live
        /// sentence plus the last play-plane aim. Runs on every send, including
        /// Recent reuse, so Asteroid / planet / You are chosen again.
        /// <para>
        /// [TITAN-ORBIT] "You" is only the hull locked when the player clicked that
        /// tile. If nobody was under the pointer, You stays empty — we do not invent
        /// a closest ship and we never fall back to the speaker (Me).
        /// </para>
        /// </summary>
        public static void BindResolvedTargets(ref ShipCommsInbox.Callout callout)
        {
            ParseWords(in callout, out ParsedWords words);
            Vector3 aim = ResolveAim(callout.NetworkId);
            TeamId speakerTeam = ReadSpeakerTeam(callout.NetworkId);
            // Only a typed Here word keeps a player-picked ping. Leftover waypoints
            // (Recent reuse, or FocusKind still None) must not freeze the old rock.
            bool keepMapPing = words.HasHere && callout.HasWaypoint != 0;

            ResolveWorldTeamPref(in words, speakerTeam, out TeamId preferTeam, out TeamId excludeTeam);
            bool hasWorldNoun = words.HasPlanet || words.HasMoon || words.HasHome
                || words.HasPad || words.HasTurret || words.HasAsteroid || words.HasGems || words.HasHere;

            bool lockYou = words.HasYou || words.HasShip || words.HasAlly
                || (words.Hostile && !hasWorldNoun && !words.HasThem && !words.HasUs
                    && !words.HasEscort && !words.HasEveryone);
            if (lockYou)
            {
                // --- You / ship seat ---
                // Pending You is the click-lock from the compose matrix. The speaker
                // can never be "You" — that word means another hull.
                int locked = 0;
                if (ShipCommsClientState.HasPendingYou)
                    locked = ShipCommsClientState.PendingYouNetworkId;
                if (locked == callout.NetworkId)
                    locked = 0;

                bool teammatesOnly = words.Friendly && !words.Hostile && !words.HasAlly && !words.HasEnemy;
                bool enemiesOnly = words.Hostile || words.HasAlly || words.HasEnemy;
                if (words.HasYou && !words.Hostile && !words.Friendly && !words.HasAlly && !words.HasEnemy)
                {
                    teammatesOnly = false;
                    enemiesOnly = false;
                }

                // Ship / Ally / a bare hostile verb still pick the closest hull in range.
                // A typed You word does not — empty means empty.
                if (locked <= 0 && !words.HasYou)
                {
                    int n = CollectClosest(
                        aim, YouSelectRange, callout.NetworkId, speakerTeam,
                        teammatesOnly, enemiesOnly, s_IdScratch, 1);
                    locked = n > 0 ? s_IdScratch[0] : 0;
                }

                if (locked == callout.NetworkId)
                    locked = 0;
                callout.YouNetworkId = locked;
            }

            callout.Everyone = words.HasEveryone || words.HasTeam ? (byte)1 : (byte)0;

            if (keepMapPing)
            {
                callout.FocusKind = ShipCommsInbox.FocusKind.MapPing;
                BindHerePlanetName(ref callout);
            }
            else if (words.HasHere)
            {
                callout.HasWaypoint = 1;
                callout.WaypointX = aim.x;
                callout.WaypointZ = aim.z;
                callout.FocusKind = ShipCommsInbox.FocusKind.MapPing;
                BindHerePlanetName(ref callout);
            }
            else
            {
                // Gems never fall back to an asteroid. Each world noun binds on its
                // own so "Asteroid Moon" can store a rock waypoint and a moon planet id.
                var viz = EcsWorldVisualizer.Active;
                bool boundPickup = false;
                if (words.HasGems
                    && viz != null
                    && viz.TryFindClosestGem(aim, YouSelectRange, out Vector3 gem, out _))
                {
                    callout.HasWaypoint = 1;
                    callout.WaypointX = gem.x;
                    callout.WaypointZ = gem.z;
                    callout.FocusKind = ShipCommsInbox.FocusKind.Gem;
                    boundPickup = true;
                }
                else if (words.HasAsteroid && viz != null)
                {
                    // Color words are a hard filter — "Red Asteroid" must not fall back
                    // to the nearest unowned / other-team rock.
                    TeamId rockTeam = words.ColorTeam;
                    TeamId rockExclude = rockTeam == TeamId.None ? excludeTeam : TeamId.None;
                    bool allowFallback = rockTeam == TeamId.None && rockExclude == TeamId.None;
                    if (viz.TryFindClosestAsteroid(
                            aim, rockTeam, out Vector3 rock, out _,
                            rockExclude, allowFallback))
                    {
                        callout.HasWaypoint = 1;
                        callout.WaypointX = rock.x;
                        callout.WaypointZ = rock.z;
                        callout.FocusKind = ShipCommsInbox.FocusKind.Asteroid;
                        boundPickup = true;
                    }
                }

                if (words.HasMoon)
                {
                    bool homeMoon = words.HasHome;
                    if (PlanetGemMoonVisualRegistry.TryFindClosestMoon(
                        aim, preferTeam, homeMoon, out int moonPlanetId, out Vector3 moonPos,
                        allowFallback: !homeMoon && preferTeam == TeamId.None && excludeTeam == TeamId.None,
                        excludeTeam))
                    {
                        callout.PlanetId = moonPlanetId;
                        if (!boundPickup)
                        {
                            callout.HasWaypoint = 1;
                            callout.WaypointX = moonPos.x;
                            callout.WaypointZ = moonPos.z;
                            callout.FocusKind = ShipCommsInbox.FocusKind.Moon;
                        }
                    }
                }
                else if (words.HasTurret)
                {
                    if (PlanetaryDefenseVisualDriver.TryFindClosestDefenseSlot(
                        aim, preferTeam, turretOnly: true,
                        out int turretPlanetId, out Vector3 turretPos, out _,
                        excludeTeam, allowFallback: preferTeam == TeamId.None && excludeTeam == TeamId.None))
                    {
                        callout.PlanetId = turretPlanetId;
                        if (!boundPickup)
                        {
                            callout.HasWaypoint = 1;
                            callout.WaypointX = turretPos.x;
                            callout.WaypointZ = turretPos.z;
                            callout.FocusKind = ShipCommsInbox.FocusKind.Turret;
                        }
                    }
                }
                else if (words.HasPad)
                {
                    if (PlanetaryDefenseVisualDriver.TryFindClosestDefenseSlot(
                        aim, preferTeam, turretOnly: false,
                        out int padPlanetId, out Vector3 padPos, out _,
                        excludeTeam, allowFallback: preferTeam == TeamId.None && excludeTeam == TeamId.None))
                    {
                        callout.PlanetId = padPlanetId;
                        if (!boundPickup)
                        {
                            callout.HasWaypoint = 1;
                            callout.WaypointX = padPos.x;
                            callout.WaypointZ = padPos.z;
                            callout.FocusKind = ShipCommsInbox.FocusKind.Pad;
                        }
                    }
                }
                else if (words.HasPlanet || words.HasHome)
                {
                    if (viz != null
                        && viz.TryFindClosestPlanet(
                            aim, preferTeam, words.HasHome, out int planetId, out Vector3 planetPos,
                            excludeTeam, allowFallback: preferTeam == TeamId.None && excludeTeam == TeamId.None && !words.HasHome))
                    {
                        callout.PlanetId = planetId;
                        if (!boundPickup)
                        {
                            callout.HasWaypoint = 1;
                            callout.WaypointX = planetPos.x;
                            callout.WaypointZ = planetPos.z;
                            callout.FocusKind = ShipCommsInbox.FocusKind.Planet;
                        }
                    }
                }
            }

            SnapshotFrozen(ref callout, in words, speakerTeam, aim);
        }

        /// <summary>
        /// Stamps the planet disc the player clicked so Here chips can show Helios
        /// instead of HERE. Leaves FocusKind as a map ping so Recent still replays
        /// the point. PlanetId 0 keeps the generic Here word.
        /// </summary>
        static void BindHerePlanetName(ref ShipCommsInbox.Callout callout)
        {
            if (ShipCommsClientState.PendingPlanetId <= 0)
                return;
            callout.PlanetId = ShipCommsClientState.PendingPlanetId;
        }

        /// <summary>
        /// Replaces a Here catalog label with the locked planet's world name.
        /// Compose uses the pending click; live bubbles use the callout PlanetId.
        /// </summary>
        public static bool TryResolveHereDisplayLabel(
            string catalogLabel, int planetId, bool isHomePlanet, int familyIndex, out string display)
        {
            display = catalogLabel;
            if (planetId <= 0 || !Eq(catalogLabel, "Here"))
                return false;

            var config = PlanetShipFamilyConfig.LoadDefault();
            string name = config != null
                ? config.GetPlanetDisplayName(planetId, isHomePlanet, familyIndex)
                : PlanetDisplayNames.Resolve(planetId, isHomePlanet);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            display = name.Trim();
            return true;
        }

        /// <summary>
        /// Bubble / RPC path: infer home vs neutral from the ghosted PlanetId
        /// (homes are team ids 1–5, neutrals start at 100).
        /// </summary>
        public static bool TryResolveHereDisplayLabel(
            in ShipCommsInbox.Callout callout, string catalogLabel, out string display)
        {
            bool isHome = callout.PlanetId > 0
                && callout.PlanetId < PlanetDisplayNames.NeutralPlanetIdBase;
            return TryResolveHereDisplayLabel(catalogLabel, callout.PlanetId, isHome, -1, out display);
        }

        /// <summary>Compose-rail path: pending minimap planet click, or catalog Here.</summary>
        public static bool TryResolvePendingHereDisplayLabel(string catalogLabel, out string display)
        {
            return TryResolveHereDisplayLabel(
                catalogLabel,
                ShipCommsClientState.PendingPlanetId,
                ShipCommsClientState.PendingPlanetIsHome,
                ShipCommsClientState.PendingPlanetFamilyIndex,
                out display);
        }

        /// <summary>
        /// Locks Us network ids at send and writes XZ fallbacks if a hull later despawns.
        /// Draw follows live proxies — it does not stay on these seats.
        /// <para>
        /// [TITAN-ORBIT] Us is teammates inside <see cref="YouSelectRange"/> of the
        /// speaker hull. Escort is every teammate that has troops aboard and sits
        /// inside that same range of the play-plane mouse aim. Empty Us / Escort
        /// stays empty (same as empty You).
        /// </para>
        /// </summary>
        static void SnapshotFrozen(
            ref ShipCommsInbox.Callout callout,
            in ParsedWords words,
            TeamId speakerTeam,
            Vector3 aim)
        {
            Vector3 me = default;
            if (TryHullPos(callout.NetworkId, out me))
            {
                callout.MeX = me.x;
                callout.MeZ = me.z;
            }

            if (callout.YouNetworkId > 0 && TryHullPos(callout.YouNetworkId, out Vector3 you))
            {
                callout.YouX = you.x;
                callout.YouZ = you.z;
            }

            int n = 0;
            if (words.HasEveryone || words.HasTeam)
            {
                n = ShipWeaponProxyRegistry.CollectHullsOnTeam(
                    speakerTeam, s_IdScratch, s_PosScratch, MaxEveryone);
            }
            else if (words.HasEscort)
            {
                // --- Troop carriers around the mouse ---
                // Exclude nobody: a loaded speaker under the pointer is still an escort.
                // Cap at MaxEveryone (G0–G7). When You is unused we stash the aim in
                // YouX/Z so remotes can re-collect the same circle later.
                n = CollectClosest(
                    aim, YouSelectRange, exclude: 0, speakerTeam,
                    teammatesOnly: true, enemiesOnly: false, s_IdScratch, MaxEveryone,
                    troopCarriersOnly: true);
                for (int i = 0; i < n; i++)
                {
                    if (!TryHullPos(s_IdScratch[i], out s_PosScratch[i]))
                        s_PosScratch[i] = Vector3.zero;
                }

                for (int i = 0; i < MaxUsLocks; i++)
                    SetUsId(ref callout, i, i < n ? s_IdScratch[i] : 0);

                if (callout.YouNetworkId <= 0)
                {
                    callout.YouX = aim.x;
                    callout.YouZ = aim.z;
                }
            }
            else if (words.HasUs)
            {
                // --- Friendlies around the speaker ---
                // Origin is the speaker hull written above (Me XZ if the proxy is
                // gone). Aim is only a last-ditch fallback when we have no pose.
                Vector3 usOrigin = me.x != 0f || me.z != 0f
                    ? me
                    : new Vector3(callout.MeX, 0f, callout.MeZ);
                if (usOrigin.x == 0f && usOrigin.z == 0f)
                    usOrigin = aim;

                n = CollectClosest(
                    usOrigin, YouSelectRange, callout.NetworkId, speakerTeam,
                    teammatesOnly: true, enemiesOnly: false, s_IdScratch, MaxUsLocks);
                for (int i = 0; i < n; i++)
                {
                    if (!TryHullPos(s_IdScratch[i], out s_PosScratch[i]))
                        s_PosScratch[i] = Vector3.zero;
                }

                for (int i = 0; i < MaxUsLocks; i++)
                    SetUsId(ref callout, i, i < n ? s_IdScratch[i] : 0);
            }
            else if (words.HasThem)
            {
                // Stale "Them" label only — the matrix no longer shows that chip.
                n = CollectClosest(
                    aim, YouSelectRange, callout.NetworkId, speakerTeam,
                    teammatesOnly: false, enemiesOnly: true, s_IdScratch, MaxUsLocks);
                for (int i = 0; i < n; i++)
                {
                    if (!TryHullPos(s_IdScratch[i], out s_PosScratch[i]))
                        s_PosScratch[i] = Vector3.zero;
                }

                for (int i = 0; i < MaxUsLocks; i++)
                    SetUsId(ref callout, i, i < n ? s_IdScratch[i] : 0);
            }

            callout.GroupCount = (byte)n;
            for (int i = 0; i < MaxEveryone; i++)
            {
                Vector3 p = i < n ? s_PosScratch[i] : Vector3.zero;
                SetGroup(ref callout, i, p);
            }
        }

        /// <summary>Closest other ship to <paramref name="aim"/> within <see cref="YouSelectRange"/>. Never the speaker.</summary>
        public static bool TryResolveYou(Vector3 aim, int excludeNetworkId, out int networkId)
        {
            return TryResolveYou(aim, excludeNetworkId, TeamId.None, false, false, out networkId);
        }

        public static bool TryResolveYou(
            Vector3 aim, int excludeNetworkId, TeamId speakerTeam, bool teammatesOnly, bool enemiesOnly,
            out int networkId)
        {
            networkId = 0;
            int n = ShipWeaponProxyRegistry.CollectClosestHulls(
                aim, YouSelectRange, excludeNetworkId, speakerTeam, teammatesOnly, enemiesOnly, s_IdScratch, 1);
            if (n <= 0)
                return false;
            networkId = s_IdScratch[0];
            return networkId > 0 && networkId != excludeNetworkId;
        }

        /// <summary>
        /// NetworkIds tagged by a commander sentence: Everyone / Team (whole squad),
        /// Us / Them (locked seats), and You. Never includes the speaker — they already
        /// have chips above their hull. Used by the bubble presenter to plant echo chips
        /// under each affected ship.
        /// </summary>
        /// <param name="callout">Resolved RPC payload (Us / You already bound on send).</param>
        /// <param name="dst">Caller buffer. Writes unique ids, speaker excluded.</param>
        /// <returns>How many ids were written (0 when the sentence tags no other ship).</returns>
        public static int CollectTaggedPlayerIds(in ShipCommsInbox.Callout callout, int[] dst)
        {
            if (dst == null || dst.Length == 0)
                return 0;

            ParseWords(in callout, out ParsedWords words);
            int speaker = callout.NetworkId;
            int n = 0;

            // --- Squad words ---
            // Everyone / Team re-collect live teammates so a late joiner still gets the echo.
            if (words.HasEveryone || words.HasTeam)
            {
                TeamId team = ReadSpeakerTeam(speaker);
                int gathered = ShipWeaponProxyRegistry.CollectHullsOnTeam(
                    team, s_TagIdScratch, s_TagPosScratch, MaxTaggedPlayers);
                for (int i = 0; i < gathered; i++)
                    AddUniqueTagged(dst, ref n, s_TagIdScratch[i], speaker);
                return n;
            }

            // --- Locked lists ---
            // Escort re-collects live troop carriers around the stored mouse aim.
            // Us / Them seats are frozen on send (Us0–Us3). You is a single lock.
            if (words.HasEscort)
            {
                Vector3 origin = ResolveEscortAim(in callout);
                TeamId team = ReadSpeakerTeam(speaker);
                int gathered = CollectClosest(
                    origin, YouSelectRange, exclude: 0, team,
                    teammatesOnly: true, enemiesOnly: false, s_TagIdScratch, MaxTaggedPlayers,
                    troopCarriersOnly: true);
                for (int i = 0; i < gathered; i++)
                    AddUniqueTagged(dst, ref n, s_TagIdScratch[i], speaker);
            }
            else if (words.HasUs || words.HasThem)
            {
                for (int i = 0; i < MaxUsLocks; i++)
                    AddUniqueTagged(dst, ref n, GetUsId(in callout, i), speaker);
            }

            if (callout.YouNetworkId > 0)
                AddUniqueTagged(dst, ref n, callout.YouNetworkId, speaker);

            return n;
        }

        /// <summary>Appends <paramref name="id"/> when it is a real other ship not already listed.</summary>
        static void AddUniqueTagged(int[] dst, ref int n, int id, int speaker)
        {
            if (id <= 0 || id == speaker || n >= dst.Length)
                return;
            for (int i = 0; i < n; i++)
            {
                if (dst[i] == id)
                    return;
            }

            dst[n++] = id;
        }

        /// <summary>True when the local viewer is the speaker or on the speaker's team.</summary>
        public static bool LocalViewerCanSeePaths(int speakerNetworkId)
        {
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0 && localId == speakerNetworkId)
                return true;
            TeamId local = ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);
            TeamId speaker = ReadSpeakerTeam(speakerNetworkId);
            return local != TeamId.None && speaker != TeamId.None && local == speaker;
        }

        static void ResolveWorldTeamPref(
            in ParsedWords words, TeamId speakerTeam, out TeamId preferTeam, out TeamId excludeTeam)
        {
            preferTeam = TeamId.None;
            excludeTeam = TeamId.None;
            if (words.ColorTeam != TeamId.None)
            {
                preferTeam = words.ColorTeam;
                return;
            }

            if (words.Hostile && !words.Friendly)
                excludeTeam = speakerTeam;
            else if (words.Friendly && !words.Hostile)
                preferTeam = speakerTeam;
            else if (words.Hostile)
                excludeTeam = speakerTeam;
            else if (speakerTeam != TeamId.None)
                preferTeam = speakerTeam;
        }

        /// <summary>Action-word tint used by both path lines and matrix tile fonts.</summary>
        public static readonly Color ChipIdleFill = new Color(0.03f, 0.05f, 0.09f, 0.96f);
        public static readonly Color ChipSelectedFill = new Color(0.05f, 0.12f, 0.22f, 0.98f);
        public static readonly Color ChipLabel = new Color(0.88f, 0.92f, 0.98f, 1f);
        public static readonly Color ChipDefaultAccent = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        /// <summary>Default world-chip border — steel, not cyan or team-channel amber.</summary>
        public static readonly Color ChipNeutralFrame = new Color(0.40f, 0.48f, 0.56f, 0.90f);

        public static bool TryGetLineColor(string label, out Color color)
        {
            return TryActionColor(label, out color);
        }

        /// <summary>
        /// Same fill / font / accent as a matrix tile so world chips match the HUD.
        /// Team words get a faction wash; tactical words keep a grey button and colored font.
        /// </summary>
        public static void ResolveChipPaint(
            string label, bool selected, out Color fill, out Color labelColor, out Color accent)
        {
            accent = ChipDefaultAccent;
            bool isTeam = TeamIdExtensions.TryParseColorName(label, out TeamId team);
            bool isCommander = ShipCommsKeywordCatalog.IsCommanderLabel(label);
            bool isLine = false;
            if (isTeam)
                accent = team.ToColor();
            else if (isCommander)
                accent = TeamCommanderRules.Gold;
            else if (TryActionColor(label, out Color line))
            {
                accent = line;
                isLine = true;
            }

            bool wash = isTeam || isCommander;
            Color idle = wash ? Color.Lerp(ChipIdleFill, accent, 0.28f) : ChipIdleFill;
            Color picked = wash ? Color.Lerp(ChipSelectedFill, accent, 0.4f) : ChipSelectedFill;
            fill = selected ? picked : idle;
            labelColor = wash || isLine
                ? Color.Lerp(ChipLabel, accent, isLine ? 0.75f : 0.55f)
                : ChipLabel;
        }

        /// <summary>
        /// World-chip border: faction / action tint when that word owns a color, otherwise steel.
        /// Team-only channel is not a border color.
        /// </summary>
        public static Color ResolveChipFrameColor(string label)
        {
            if (TeamIdExtensions.TryParseColorName(label, out TeamId team))
                return team.ToColor();
            if (ShipCommsKeywordCatalog.IsCommanderLabel(label))
                return TeamCommanderRules.Gold;
            if (TryActionColor(label, out Color line))
                return line;
            return ChipNeutralFrame;
        }

        /// <summary>Action-word line color for a live callout, or false when the path stays white.</summary>
        public static bool TryGetCalloutLineColor(in ShipCommsInbox.Callout callout, out Color color)
        {
            color = ResolveActionColor(in callout);
            return color.a > 0.01f && (color.r + color.g + color.b) < 2.85f;
        }

        /// <summary>
        /// Used by the compose panel to lock targets on click.
        /// </summary>
        public static bool IsAnchorLabel(string label)
        {
            Classify(label, out WordKind kind, out _);
            return kind != WordKind.None && kind != WordKind.Action && kind != WordKind.Color;
        }

        /// <summary>
        /// True when this sentence used a command-deck word (Everyone, Form Up, …)
        /// so the path may linger after the 4s chip row fades. Channel-only
        /// commander chrome without those words still expires with the chips.
        /// </summary>
        /// <param name="callout">Live inbox row (speaker + keyword bytes).</param>
        public static bool ShouldLingerCommanderPaths(in ShipCommsInbox.Callout callout)
        {
            if (callout.Count < 1)
                return false;
            if (TeamCommanderRules.Sanitize(callout.TeamOnly) != ShipCommsChannel.Commander)
                return false;

            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            return catalog != null
                && catalog.SequenceUsesCommanderKeyword(
                    callout.Count, callout.K0, callout.K1, callout.K2, callout.K3, callout.K4);
        }

        /// <summary>
        /// How the path should look at <paramref name="age"/>. Before
        /// <paramref name="chipLifetime"/> the stroke is full rank width and the
        /// grow-and-repeat travel pulse stays on. After that (commander linger)
        /// the line is thinner, partly transparent, and drawn full-length so it
        /// stops flashing.
        /// </summary>
        /// <param name="age">Seconds since this callout appeared.</param>
        /// <param name="chipLifetime">Chip-row lifetime (4s). Linger starts here.</param>
        /// <param name="lingerSeconds">Extra seconds after chips fade. 0 = no linger.</param>
        /// <param name="strokeScale">1 while live; &lt;1 during linger.</param>
        /// <param name="lineAlpha">Core opacity for world and minimap strokes.</param>
        /// <param name="useTravelPulse">False during linger so the ghost stays still.</param>
        /// <returns>False when this callout should no longer draw.</returns>
        public static bool TryResolvePathPresentation(
            float age,
            float chipLifetime,
            float lingerSeconds,
            out float strokeScale,
            out float lineAlpha,
            out bool useTravelPulse)
        {
            strokeScale = 1f;
            lineAlpha = PathLineLiveAlpha;
            useTravelPulse = true;

            float expireAt = chipLifetime + Mathf.Max(0f, lingerSeconds);
            if (age >= expireAt)
                return false;

            // --- Live chips ---
            // Same look as before: thick rank stroke, on/off travel pulse.
            if (age < chipLifetime || lingerSeconds <= 0f)
                return true;

            // --- Commander ghost ---
            // [TITAN-ORBIT] After the message chips fade, keep the path stroke as a
            // quiet reminder. Rings stay off. No travel pulse — a repeating grow
            // would stay loud.
            useTravelPulse = false;
            strokeScale = PathLineLingerStrokeScale;
            lineAlpha = PathLineLingerAlpha;

            float fadeStart = expireAt - PathLingerFadeSeconds;
            if (age > fadeStart && PathLingerFadeSeconds > 0.01f)
                lineAlpha *= 1f - Mathf.Clamp01((age - fadeStart) / PathLingerFadeSeconds);

            return lineAlpha > 0.01f;
        }

        /// <summary>
        /// Shrinks a resolved rank stroke for the linger window. Shared by world
        /// Shapes lines and the minimap overlay so both stay in lockstep.
        /// </summary>
        /// <param name="corePx">Rank core thickness (pixels). Scaled in place.</param>
        /// <param name="outlinePx">Optional outline thickness. Scaled in place.</param>
        public static void ApplyLingerStroke(ref float corePx, ref float outlinePx)
        {
            corePx = Mathf.Max(PathLineLingerMinPixels, corePx * PathLineLingerStrokeScale);
            outlinePx *= PathLineLingerStrokeScale;
        }

        /// <summary>
        /// On/off path lines + waypoint discs for one callout. Must run inside an
        /// existing Shapes <c>Draw.Command</c>. Hulls and planets are live; ping /
        /// asteroid stay frozen. After <paramref name="lifetime"/> a commander
        /// sentence keeps a thinner, quieter path ghost for
        /// <see cref="CommanderPathLingerSeconds"/> — rings do not linger.
        /// </summary>
        public static void DrawIntent(in ShipCommsInbox.Callout callout, float age, float lifetime, float alpha)
        {
            if (callout.Count < 1)
                return;
            if (!LocalViewerCanSeePaths(callout.NetworkId))
                return;

            float lingerSeconds = ShouldLingerCommanderPaths(in callout)
                ? CommanderPathLingerSeconds
                : 0f;
            if (!TryResolvePathPresentation(
                    age, lifetime, lingerSeconds,
                    out float strokeScale, out float lineAlpha, out bool useTravelPulse))
                return;

            ParseWords(in callout, out ParsedWords words);
            Color color = ResolveActionColor(in words);
            ResolveLineStroke(callout.NetworkId, forMinimap: false, out float corePx, out float outlinePx, out Color outline);
            if (strokeScale < 0.999f)
                ApplyLingerStroke(ref corePx, ref outlinePx);

            int pathCount = BuildPaths(in callout, in words, s_PathFrom, s_PathTo, s_PathFromR, s_PathToR, MaxPaths);

            bool drawStroke = pathCount > 0;
            float travelT = 1f;
            if (drawStroke && useTravelPulse)
                drawStroke = TryGetTravelT(age, out travelT);

            if (drawStroke)
            {
                Draw.ThicknessSpace = ThicknessSpace.Pixels;
                for (int p = 0; p < pathCount; p++)
                {
                    Color line = s_PathColor[p].a > 0.01f ? s_PathColor[p] : color;
                    line.a = lineAlpha;
                    Vector3 from = Lift(s_PathFrom[p]);
                    Vector3 to = Lift(UnwrapToward(s_PathFrom[p], s_PathTo[p]));
                    if (!TryTrimToRings(from, to, s_PathFromR[p], s_PathToR[p], out Vector3 a, out Vector3 b))
                        continue;
                    if (!TryTravelSegment(a, b, travelT, out Vector3 sa, out Vector3 sb))
                        continue;
                    if (outlinePx > corePx)
                    {
                        Color ghostOutline = outline;
                        ghostOutline.a *= lineAlpha / PathLineLiveAlpha;
                        Draw.Line(sa, sb, outlinePx, LineEndCap.None, ghostOutline);
                    }
                    Draw.Line(sa, sb, corePx, LineEndCap.None, line);
                }
            }

            // Rings are part of the 4s message, not the ghost. After chips fade,
            // only the path stroke stays.
            if (age < lifetime)
                DrawNodes(in callout, in words, color, alpha, pathCount);
        }

        /// <summary>Compose-time pointer on a locked "You" hull.</summary>
        public static void DrawPendingYou(float alpha)
        {
            if (!ShipCommsClientState.HasPendingYou)
                return;
            if (!TryHullPos(ShipCommsClientState.PendingYouNetworkId, out Vector3 pos))
                return;

            Color c = new Color(0.35f, 0.72f, 0.95f, 0.85f * alpha);
            DrawHollowRing(
                Lift(pos),
                HullRadius(ShipCommsClientState.PendingYouNetworkId),
                c,
                ShipCommsClientState.PendingYouNetworkId,
                RingThicknessPixels);
        }

        /// <summary>0–1 grow along the path this cycle, or false during the short gap.</summary>
        public static bool TryGetTravelT(float age, out float t)
        {
            float cycle = PathTravelOnSeconds + PathTravelGapSeconds;
            if (cycle < 0.05f)
            {
                t = 1f;
                return true;
            }

            float u = age % cycle;
            if (u >= PathTravelOnSeconds)
            {
                t = 0f;
                return false;
            }

            t = u / PathTravelOnSeconds;
            return true;
        }

        static bool TryTravelSegment(Vector3 from, Vector3 to, float t, out Vector3 a, out Vector3 b)
        {
            a = from;
            b = Vector3.Lerp(from, to, Mathf.Clamp01(t));
            return (b - a).sqrMagnitude > 0.0004f;
        }

        /// <summary>
        /// Live path segments for the minimap (already ring-trimmed, shortest wrap).
        /// Returns how many entries were written from <paramref name="start"/>.
        /// Commander linger writes a quieter alpha and sets <paramref name="linger"/>
        /// so the map stroke can shrink to match the world ghost.
        /// </summary>
        /// <param name="chipLifetime">Same 4s chip window the world drawer uses.</param>
        /// <param name="linger">Optional per-slot flag. True = apply linger stroke.</param>
        public static int CopyVisiblePaths(
            in ShipCommsInbox.Callout callout,
            float age,
            float chipLifetime,
            Vector3[] from,
            Vector3[] to,
            Color[] colors,
            int[] ranks,
            bool[] linger,
            int start,
            int max)
        {
            if (from == null || to == null || colors == null || start >= max)
                return 0;
            if (!LocalViewerCanSeePaths(callout.NetworkId))
                return 0;

            float lingerSeconds = ShouldLingerCommanderPaths(in callout)
                ? CommanderPathLingerSeconds
                : 0f;
            if (!TryResolvePathPresentation(
                    age, chipLifetime, lingerSeconds,
                    out _, out float lineAlpha, out bool useTravelPulse))
                return 0;

            float travelT = 1f;
            if (useTravelPulse && !TryGetTravelT(age, out travelT))
                return 0;

            ParseWords(in callout, out ParsedWords words);
            int n = BuildPaths(in callout, in words, s_PathFrom, s_PathTo, s_PathFromR, s_PathToR, MaxPaths);
            if (n <= 0)
                return 0;

            Color fallback = ResolveActionColor(in words);
            int rank = ReadSpeakerTeamRank(callout.NetworkId);
            bool lingering = lingerSeconds > 0f && age >= chipLifetime;
            int written = 0;
            for (int i = 0; i < n && start + written < max; i++)
            {
                Vector3 a = s_PathFrom[i];
                Vector3 b = UnwrapToward(a, s_PathTo[i]);
                if (!TryTrimToRings(a, b, s_PathFromR[i], s_PathToR[i], out Vector3 ta, out Vector3 tb))
                    continue;
                if (!TryTravelSegment(ta, tb, travelT, out Vector3 sa, out Vector3 sb))
                    continue;
                int slot = start + written;
                from[slot] = sa;
                to[slot] = sb;
                Color painted = s_PathColor[i].a > 0.01f ? s_PathColor[i] : fallback;
                painted.a = lineAlpha;
                colors[slot] = painted;
                if (ranks != null)
                    ranks[slot] = rank;
                if (linger != null)
                    linger[slot] = lingering;
                written++;
            }

            return written;
        }

        /// <summary>1-based team rank of the speaker, or 0 when unknown.</summary>
        public static int ReadSpeakerTeamRank(int networkId)
        {
            return ShipMatchScoreLogic.TryGetTeamRank(networkId, out int rank) ? rank : 0;
        }

        /// <summary>
        /// Stroke width for the speaker's team rank. Top 3 are thicker; no medal outline.
        /// </summary>
        public static void ResolveLineStroke(
            int networkId, bool forMinimap, out float corePx, out float outlinePx, out Color outlineColor)
        {
            int rank = ReadSpeakerTeamRank(networkId);
            ResolveLineStrokeForRank(rank, forMinimap, out corePx, out outlinePx, out outlineColor);
        }

        public static void ResolveLineStrokeForRank(
            int teamRank, bool forMinimap, out float corePx, out float outlinePx, out Color outlineColor)
        {
            outlineColor = default;
            outlinePx = 0f;
            switch (teamRank)
            {
                case 1:
                    corePx = PathLineRank1Pixels;
                    break;
                case 2:
                    corePx = PathLineRank2Pixels;
                    break;
                case 3:
                    corePx = PathLineRank3Pixels;
                    break;
                default:
                    corePx = PathLineThinPixels;
                    break;
            }

            if (forMinimap)
            {
                corePx *= PathLineMinimapScale;
                outlinePx *= PathLineMinimapScale;
            }
        }

        static int BuildPaths(
            in ShipCommsInbox.Callout callout,
            in ParsedWords words,
            Vector3[] from,
            Vector3[] to,
            float[] fromR,
            float[] toR,
            int maxPaths)
        {
            int nodeCount = CollectOrderedAnchors(in callout, in words, s_AnchorScratch, s_AnchorInbound);
            nodeCount = CompactAnchors(in callout, in words, s_AnchorScratch, s_AnchorInbound, nodeCount);
            s_LastNodeCount = nodeCount;
            if (nodeCount <= 0)
                return 0;

            Color fallback = ResolveActionColor(in callout);
            int written = 0;

            // Word order is direction: first noun → next. "Me Asteroid Moon" is two segments.
            for (int i = 0; i < nodeCount - 1 && written < maxPaths; i++)
            {
                int fromN = ExpandAnchor(in callout, in words, s_AnchorScratch[i], s_ExpandA, s_RadiusA);
                int toN = ExpandAnchor(in callout, in words, s_AnchorScratch[i + 1], s_ExpandB, s_RadiusB);
                if (fromN <= 0 || toN <= 0)
                    continue;

                Color seg = s_AnchorInbound[i + 1].a > 0.01f ? s_AnchorInbound[i + 1] : fallback;

                if (fromN == 1 && toN == 1)
                {
                    WritePath(from, to, fromR, toR, ref written,
                        s_ExpandA[0], s_ExpandB[0], s_RadiusA[0], s_RadiusB[0], seg);
                    continue;
                }

                if (fromN > 1 && toN == 1)
                {
                    for (int a = 0; a < fromN && written < maxPaths; a++)
                    {
                        WritePath(from, to, fromR, toR, ref written,
                            s_ExpandA[a], s_ExpandB[0], s_RadiusA[a], s_RadiusB[0], seg);
                    }

                    continue;
                }

                if (fromN == 1 && toN > 1)
                {
                    for (int b = 0; b < toN && written < maxPaths; b++)
                    {
                        WritePath(from, to, fromR, toR, ref written,
                            s_ExpandA[0], s_ExpandB[b], s_RadiusA[0], s_RadiusB[b], seg);
                    }

                    continue;
                }

                for (int a = 0; a < fromN && written < maxPaths; a++)
                {
                    int tb = a % toN;
                    WritePath(from, to, fromR, toR, ref written,
                        s_ExpandA[a], s_ExpandB[tb], s_RadiusA[a], s_RadiusB[tb], seg);
                }
            }

            // Lone world noun still gets Me → target so a solo "Moon" reads.
            // [TITAN-ORBIT] Empty You / Us is a real choice: "You Asteroid" or
            // "Us Asteroid" with no locked hull must not grow a speaker line.
            if (written == 0
                && !NamedShipSourceIsEmpty(in callout, in words)
                && TryLiveMe(in callout, out Vector3 speaker))
            {
                Color loneColor = fallback;
                if (nodeCount >= 1 && s_AnchorInbound[0].a > 0.01f)
                    loneColor = s_AnchorInbound[0];
                if (TryFocusPos(in callout, in words, out Vector3 lone, out float loneR))
                {
                    WritePath(from, to, fromR, toR, ref written,
                        speaker, lone, HullRadius(callout.NetworkId), loneR, loneColor);
                }
                else if (TryLiveYou(in callout, out Vector3 you))
                {
                    WritePath(from, to, fromR, toR, ref written,
                        speaker, you, HullRadius(callout.NetworkId), HullRadius(callout.YouNetworkId), loneColor);
                }
            }

            return written;
        }

        /// <summary>
        /// Ordered anchor kinds as bytes (see <see cref="AnchorId"/>). Color / action
        /// words are skipped so "Me Heal You" is Me then You. Each noun stores the
        /// last verb color before it so Me Mining Asteroid Deposit Moon can tint
        /// each segment separately.
        /// </summary>
        static int CollectOrderedAnchors(
            in ShipCommsInbox.Callout callout,
            in ParsedWords words,
            int[] dst,
            Color[] inbound)
        {
            int n = 0;
            Color pending = default;
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            CollectWord(catalog, callout.K0, callout.Count >= 1, in words, dst, inbound, ref n, ref pending);
            CollectWord(catalog, callout.K1, callout.Count >= 2, in words, dst, inbound, ref n, ref pending);
            CollectWord(catalog, callout.K2, callout.Count >= 3, in words, dst, inbound, ref n, ref pending);
            CollectWord(catalog, callout.K3, callout.Count >= 4, in words, dst, inbound, ref n, ref pending);
            CollectWord(catalog, callout.K4, callout.Count >= 5, in words, dst, inbound, ref n, ref pending);

            if (callout.HasWaypoint != 0
                || callout.FocusKind == ShipCommsInbox.FocusKind.Planet
                || callout.FocusKind == ShipCommsInbox.FocusKind.Moon
                || callout.FocusKind == ShipCommsInbox.FocusKind.Pad
                || callout.FocusKind == ShipCommsInbox.FocusKind.Turret)
            {
                bool already = false;
                for (int i = 0; i < n; i++)
                {
                    if (IsWorldAnchor(dst[i]))
                    {
                        already = true;
                        break;
                    }
                }

                if (!already && n < dst.Length)
                {
                    int extra;
                    if (callout.FocusKind == ShipCommsInbox.FocusKind.Turret)
                        extra = AnchorId.Turret;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Pad)
                        extra = AnchorId.Pad;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Moon)
                        extra = AnchorId.Moon;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Planet)
                        extra = AnchorId.Planet;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Asteroid)
                        extra = AnchorId.Asteroid;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Gem)
                        extra = AnchorId.Gems;
                    else
                        extra = AnchorId.Here;
                    inbound[n] = pending;
                    dst[n++] = extra;
                }
            }

            _ = words;
            return n;
        }

        /// <summary>
        /// Drops nouns that have no live target. A lone world noun (or a chain with
        /// no speaker word) prepends Me — "Moon" is Me → friendly moon.
        /// Empty You / Us is the exception: the word stays a missing source, so we
        /// do not rewrite it as the speaker.
        /// </summary>
        static int CompactAnchors(
            in ShipCommsInbox.Callout callout,
            in ParsedWords words,
            int[] dst,
            Color[] inbound,
            int n)
        {
            // --- Drop dead nouns ---
            // ExpandAnchor returns 0 when that seat has no live pose (You with
            // NetworkId 0, a despawned moon, and so on). Those words leave the chain.
            int write = 0;
            for (int i = 0; i < n; i++)
            {
                int a = dst[i];
                if (ExpandAnchor(in callout, in words, a, s_ExpandA, s_RadiusA) <= 0)
                    continue;
                dst[write] = a;
                inbound[write] = inbound[i];
                write++;
            }

            // --- Classify survivors ---
            // hasShipSrc is a live You / Us / Everyone seat. Empty You / Us was
            // already dropped above, so it does not count as a source here.
            bool hasMe = false;
            bool hasShipSrc = false;
            bool hasWorld = false;
            for (int i = 0; i < write; i++)
            {
                int a = dst[i];
                if (a == AnchorId.Me)
                    hasMe = true;
                else if (a == AnchorId.You || a == AnchorId.Us || a == AnchorId.Everyone)
                    hasShipSrc = true;
                else if (IsWorldAnchor(a))
                    hasWorld = true;
            }

            // --- Optional Me prefix ---
            // "Asteroid" alone still reads as Me → rock. "You Asteroid" / "Us
            // Asteroid" with no locked hull already named a source — leave Me out.
            if (!hasMe
                && hasWorld
                && !hasShipSrc
                && !NamedShipSourceIsEmpty(in callout, in words)
                && write < dst.Length
                && ExpandAnchor(in callout, in words, AnchorId.Me, s_ExpandA, s_RadiusA) > 0)
            {
                for (int i = write; i > 0; i--)
                {
                    dst[i] = dst[i - 1];
                    inbound[i] = inbound[i - 1];
                }

                dst[0] = AnchorId.Me;
                inbound[0] = default;
                write++;
            }

            return write;
        }

        static void CollectWord(
            ShipCommsKeywordCatalog catalog,
            byte index,
            bool live,
            in ParsedWords words,
            int[] dst,
            Color[] inbound,
            ref int n,
            ref Color pending)
        {
            if (!live || n >= dst.Length)
                return;
            if (!catalog.TryGetLabel(index, out string label))
                return;
            Classify(label, out WordKind kind, out Color action);
            if (kind == WordKind.Action)
            {
                if (action.a > 0.01f)
                    pending = action;
                return;
            }

            // "Home Moon" is one home-moon target, not home planet then a moon.
            if (kind == WordKind.Home && words.HasMoon)
                return;
            bool worldNoun = words.HasPlanet || words.HasMoon || words.HasHome
                || words.HasPad || words.HasTurret || words.HasAsteroid || words.HasGems || words.HasHere;
            if (worldNoun && (kind == WordKind.Enemy || kind == WordKind.Ally))
                return;
            int id = KindToAnchor(kind);
            if (id == 0)
                return;
            inbound[n] = pending;
            dst[n++] = id;
            pending = default;
        }

        /// <summary>
        /// True when the sentence named You or Us and that seat has no other hull.
        /// [TITAN-ORBIT] Empty You / Us must not become Me — the player asked for
        /// another ship and got none, so the path has no source.
        /// </summary>
        static bool NamedShipSourceIsEmpty(in ShipCommsInbox.Callout callout, in ParsedWords words)
        {
            return YouSourceIsEmpty(in callout, in words)
                || UsSourceIsEmpty(in callout, in words);
        }

        /// <summary>
        /// True when the sentence named a You-style ship (You / Ship / Ally / Enemy)
        /// but that lock is empty or accidentally the speaker.
        /// </summary>
        static bool YouSourceIsEmpty(in ShipCommsInbox.Callout callout, in ParsedWords words)
        {
            bool namedYou = words.HasYou || words.HasShip || words.HasAlly || words.HasEnemy;
            if (!namedYou)
                return false;
            return callout.YouNetworkId <= 0 || callout.YouNetworkId == callout.NetworkId;
        }

        /// <summary>
        /// True when the sentence named Us / Escort and no hull was locked.
        /// Us never counts the speaker; Escort may, so any positive seat counts.
        /// </summary>
        static bool UsSourceIsEmpty(in ShipCommsInbox.Callout callout, in ParsedWords words)
        {
            if (!words.HasUs && !words.HasEscort)
                return false;
            if (words.HasEscort && callout.GroupCount > 0)
                return false;
            for (int i = 0; i < MaxUsLocks; i++)
            {
                int id = GetUsId(in callout, i);
                if (id <= 0)
                    continue;
                if (words.HasEscort || id != callout.NetworkId)
                    return false;
            }

            return true;
        }

        static bool IsWorldAnchor(int id)
        {
            return id == AnchorId.Here || id == AnchorId.Asteroid || id == AnchorId.Gems
                || id == AnchorId.Planet || id == AnchorId.Moon
                || id == AnchorId.Pad || id == AnchorId.Turret;
        }

        static void WritePath(
            Vector3[] from, Vector3[] to, float[] fromR, float[] toR, ref int written,
            Vector3 a, Vector3 b, float ra, float rb, Color color)
        {
            from[written] = a;
            to[written] = b;
            fromR[written] = VisualRingRadius(ra);
            toR[written] = VisualRingRadius(rb);
            s_PathColor[written] = color;
            written++;
        }

        static int ExpandAnchor(
            in ShipCommsInbox.Callout callout, in ParsedWords words, int anchor,
            Vector3[] dst, float[] radii)
        {
            switch (anchor)
            {
                case AnchorId.Me:
                    if (!TryLiveMe(in callout, out dst[0]))
                        return 0;
                    radii[0] = HullRadius(callout.NetworkId);
                    return 1;
                case AnchorId.You:
                    if (!TryLiveYou(in callout, out dst[0]))
                        return 0;
                    radii[0] = HullRadius(callout.YouNetworkId);
                    return 1;
                case AnchorId.Us:
                    return words.HasEscort
                        ? ReadLiveEscort(in callout, dst, radii)
                        : ReadLiveUs(in callout, dst, radii);
                case AnchorId.Everyone:
                    return ReadLiveEveryone(in callout, dst, radii);
                case AnchorId.Here:
                    return TryHerePos(in callout, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Asteroid:
                    return TryAsteroidPos(in callout, in words, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Gems:
                    return TryGemPos(in callout, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Planet:
                    return TryPlanetPos(in callout, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Moon:
                    return TryMoonPos(in callout, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Pad:
                    return TryDefensePos(in callout, turretOnly: false, out dst[0], out radii[0]) ? 1 : 0;
                case AnchorId.Turret:
                    return TryDefensePos(in callout, turretOnly: true, out dst[0], out radii[0]) ? 1 : 0;
                default:
                    _ = words;
                    return 0;
            }
        }

        static bool TryLiveMe(in ShipCommsInbox.Callout callout, out Vector3 pos)
        {
            if (TryHullPos(callout.NetworkId, out pos))
                return true;
            pos = new Vector3(callout.MeX, 0f, callout.MeZ);
            return pos.x != 0f || pos.z != 0f;
        }

        static bool TryLiveYou(in ShipCommsInbox.Callout callout, out Vector3 pos)
        {
            pos = default;
            if (callout.YouNetworkId <= 0 || callout.YouNetworkId == callout.NetworkId)
                return false;
            if (TryHullPos(callout.YouNetworkId, out pos))
                return true;
            pos = new Vector3(callout.YouX, 0f, callout.YouZ);
            return pos.x != 0f || pos.z != 0f;
        }

        /// <summary>
        /// Live troop-carrying teammates inside <see cref="YouSelectRange"/> of the
        /// stored mouse aim. Falls back to locked Us seats when You was also bound
        /// (YouX/Z is that hull, not the pointer).
        /// </summary>
        static int ReadLiveEscort(in ShipCommsInbox.Callout callout, Vector3[] dst, float[] radii)
        {
            if (dst == null)
                return 0;
            if (callout.YouNetworkId > 0)
                return ReadLiveUs(in callout, dst, radii);

            Vector3 origin = ResolveEscortAim(in callout);
            TeamId team = ReadSpeakerTeam(callout.NetworkId);
            int n = CollectClosest(
                origin, YouSelectRange, exclude: 0, team,
                teammatesOnly: true, enemiesOnly: false, s_IdScratch,
                Mathf.Min(MaxEveryone, dst.Length), troopCarriersOnly: true);
            if (n <= 0)
                return ReadFrozenGroup(in callout, dst, radii);

            for (int i = 0; i < n; i++)
            {
                if (!TryHullPos(s_IdScratch[i], out dst[i]))
                    dst[i] = origin;
                if (radii != null)
                    radii[i] = HullRadius(s_IdScratch[i]);
            }

            return n;
        }

        /// <summary>
        /// Mouse aim frozen on send in YouX/Z when You was unused, else the first
        /// locked escort seat / speaker hull.
        /// </summary>
        static Vector3 ResolveEscortAim(in ShipCommsInbox.Callout callout)
        {
            if (callout.YouNetworkId <= 0 && (callout.YouX != 0f || callout.YouZ != 0f))
                return new Vector3(callout.YouX, 0f, callout.YouZ);
            if (TryGetGroup(in callout, 0, out Vector3 first))
                return first;
            if (TryHullPos(callout.NetworkId, out Vector3 me))
                return me;
            return new Vector3(callout.MeX, 0f, callout.MeZ);
        }

        static int ReadLiveUs(in ShipCommsInbox.Callout callout, Vector3[] dst, float[] radii)
        {
            int written = 0;
            int cap = dst != null ? Mathf.Min(MaxUsLocks, dst.Length) : 0;
            for (int i = 0; i < cap; i++)
            {
                int id = GetUsId(in callout, i);
                if (id <= 0 || !TryHullPos(id, out dst[written]))
                    continue;
                if (written < s_IdScratch.Length)
                    s_IdScratch[written] = id;
                if (radii != null)
                    radii[written] = HullRadius(id);
                written++;
            }

            return written > 0 ? written : ReadFrozenGroup(in callout, dst, radii);
        }

        static int ReadLiveEveryone(in ShipCommsInbox.Callout callout, Vector3[] dst, float[] radii)
        {
            if (dst == null)
                return 0;

            TeamId team = ReadSpeakerTeam(callout.NetworkId);
            int n = ShipWeaponProxyRegistry.CollectHullsOnTeam(
                team, s_IdScratch, dst, Mathf.Min(MaxEveryone, dst.Length));
            if (n > 0)
            {
                if (radii != null)
                {
                    for (int i = 0; i < n; i++)
                        radii[i] = HullRadius(s_IdScratch[i]);
                }

                return n;
            }

            return ReadFrozenGroup(in callout, dst, radii);
        }

        static int ReadFrozenGroup(in ShipCommsInbox.Callout callout, Vector3[] dst, float[] radii)
        {
            int n = Mathf.Min(callout.GroupCount, dst.Length);
            int written = 0;
            for (int i = 0; i < n; i++)
            {
                if (!TryGetGroup(in callout, i, out dst[written]))
                    continue;
                if (radii != null)
                    radii[written] = NodeRadius;
                written++;
            }

            return written;
        }

        static void SetUsId(ref ShipCommsInbox.Callout callout, int i, int id)
        {
            switch (i)
            {
                case 0: callout.Us0 = id; break;
                case 1: callout.Us1 = id; break;
                case 2: callout.Us2 = id; break;
                case 3: callout.Us3 = id; break;
            }
        }

        static int GetUsId(in ShipCommsInbox.Callout callout, int i)
        {
            switch (i)
            {
                case 0: return callout.Us0;
                case 1: return callout.Us1;
                case 2: return callout.Us2;
                case 3: return callout.Us3;
                default: return 0;
            }
        }

        static void SetGroup(ref ShipCommsInbox.Callout callout, int i, Vector3 p)
        {
            switch (i)
            {
                case 0: callout.G0X = p.x; callout.G0Z = p.z; break;
                case 1: callout.G1X = p.x; callout.G1Z = p.z; break;
                case 2: callout.G2X = p.x; callout.G2Z = p.z; break;
                case 3: callout.G3X = p.x; callout.G3Z = p.z; break;
                case 4: callout.G4X = p.x; callout.G4Z = p.z; break;
                case 5: callout.G5X = p.x; callout.G5Z = p.z; break;
                case 6: callout.G6X = p.x; callout.G6Z = p.z; break;
                case 7: callout.G7X = p.x; callout.G7Z = p.z; break;
            }
        }

        static bool TryGetGroup(in ShipCommsInbox.Callout callout, int i, out Vector3 pos)
        {
            pos = default;
            switch (i)
            {
                case 0: pos = new Vector3(callout.G0X, 0f, callout.G0Z); break;
                case 1: pos = new Vector3(callout.G1X, 0f, callout.G1Z); break;
                case 2: pos = new Vector3(callout.G2X, 0f, callout.G2Z); break;
                case 3: pos = new Vector3(callout.G3X, 0f, callout.G3Z); break;
                case 4: pos = new Vector3(callout.G4X, 0f, callout.G4Z); break;
                case 5: pos = new Vector3(callout.G5X, 0f, callout.G5Z); break;
                case 6: pos = new Vector3(callout.G6X, 0f, callout.G6Z); break;
                case 7: pos = new Vector3(callout.G7X, 0f, callout.G7Z); break;
                default: return false;
            }

            return true;
        }

        static bool TryMoonPos(in ShipCommsInbox.Callout callout, out Vector3 pos, out float radius)
        {
            radius = NodeRadius;
            if (callout.PlanetId > 0
                && PlanetGemMoonVisualRegistry.TryGetMoon(callout.PlanetId, out var moon)
                && moon != null)
            {
                pos = moon.MoonWorldPosition;
                radius = Mathf.Max(0.15f, moon.MoonBodyRadiusWorld);
                return true;
            }

            if (callout.FocusKind == ShipCommsInbox.FocusKind.Moon)
                return TryFrozenWaypoint(in callout, out pos);
            pos = default;
            return false;
        }

        static bool TryPlanetPos(in ShipCommsInbox.Callout callout, out Vector3 pos, out float radius)
        {
            radius = NodeRadius;
            if (callout.PlanetId > 0)
            {
                var viz = EcsWorldVisualizer.Active;
                if (viz != null && viz.TryGetPlanetWorldPosition(callout.PlanetId, out pos))
                {
                    if (!viz.TryGetPlanetColliderRadius(callout.PlanetId, out radius))
                        radius = NodeRadius;
                    return true;
                }
            }

            if (callout.FocusKind == ShipCommsInbox.FocusKind.Planet)
                return TryFrozenWaypoint(in callout, out pos);
            pos = default;
            return false;
        }

        static bool TryDefensePos(
            in ShipCommsInbox.Callout callout, bool turretOnly, out Vector3 pos, out float radius)
        {
            radius = NodeRadius;
            Vector3 near = new Vector3(callout.WaypointX, 0f, callout.WaypointZ);
            if (callout.PlanetId > 0
                && PlanetaryDefenseVisualDriver.TryGetDefenseSlotPose(
                    callout.PlanetId, near, turretOnly, out pos, out radius))
                return true;

            if ((turretOnly && callout.FocusKind == ShipCommsInbox.FocusKind.Turret)
                || (!turretOnly && callout.FocusKind == ShipCommsInbox.FocusKind.Pad))
                return TryFrozenWaypoint(in callout, out pos);
            pos = default;
            return false;
        }

        static bool TryFrozenWaypoint(in ShipCommsInbox.Callout callout, out Vector3 pos)
        {
            pos = default;
            if (callout.HasWaypoint == 0)
                return false;
            pos = new Vector3(callout.WaypointX, 0f, callout.WaypointZ);
            return true;
        }

        static bool TryFocusPos(
            in ShipCommsInbox.Callout callout, in ParsedWords words, out Vector3 pos, out float radius)
        {
            radius = DefaultPingRadius;
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Moon)
                return TryMoonPos(in callout, out pos, out radius);
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Planet)
                return TryPlanetPos(in callout, out pos, out radius);
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Pad)
                return TryDefensePos(in callout, turretOnly: false, out pos, out radius);
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Turret)
                return TryDefensePos(in callout, turretOnly: true, out pos, out radius);
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Asteroid)
                return TryAsteroidPos(in callout, in words, out pos, out radius);
            if (callout.FocusKind == ShipCommsInbox.FocusKind.Gem)
                return TryGemPos(in callout, out pos, out radius);

            _ = words;
            return TryHerePos(in callout, out pos, out radius);
        }

        static bool TryHerePos(in ShipCommsInbox.Callout callout, out Vector3 pos, out float radius)
        {
            radius = DefaultPingRadius;
            return TryFrozenWaypoint(in callout, out pos);
        }

        static bool TryAsteroidPos(
            in ShipCommsInbox.Callout callout, in ParsedWords words, out Vector3 pos, out float radius)
        {
            pos = default;
            radius = NodeRadius;
            if (!words.HasAsteroid && callout.FocusKind != ShipCommsInbox.FocusKind.Asteroid)
                return false;

            int key = CalloutDrawKey(in callout);
            if (s_ExtraRockKey == key)
            {
                pos = s_ExtraRock;
                radius = s_ExtraRockR;
                return s_ExtraRockOk;
            }

            // One proxy walk per callout — radius comes from the rock's collider scale.
            s_ExtraRockKey = key;
            var viz = EcsWorldVisualizer.Active;
            Vector3 aim = callout.HasWaypoint != 0
                ? new Vector3(callout.WaypointX, 0f, callout.WaypointZ)
                : ResolveAim(callout.NetworkId);
            bool allowFallback = words.ColorTeam == TeamId.None;
            s_ExtraRockOk = viz != null
                && viz.TryFindClosestAsteroid(
                    aim, words.ColorTeam, out s_ExtraRock, out s_ExtraRockR,
                    TeamId.None, allowFallback);
            if (s_ExtraRockOk)
            {
                pos = s_ExtraRock;
                radius = s_ExtraRockR;
                return true;
            }

            if (callout.FocusKind == ShipCommsInbox.FocusKind.Asteroid
                && TryFrozenWaypoint(in callout, out pos))
                return true;
            return false;
        }

        static bool TryGemPos(in ShipCommsInbox.Callout callout, out Vector3 pos, out float radius)
        {
            radius = DefaultPingRadius;
            pos = default;
            if (callout.FocusKind != ShipCommsInbox.FocusKind.Gem)
                return false;
            return TryFrozenWaypoint(in callout, out pos);
        }

        static int CalloutDrawKey(in ShipCommsInbox.Callout callout)
        {
            unchecked
            {
                int h = callout.NetworkId;
                h = h * 31 + callout.Count;
                h = h * 31 + callout.K0;
                h = h * 31 + callout.K1;
                h = h * 31 + callout.K2;
                h = h * 31 + callout.K3;
                h = h * 31 + callout.K4;
                h = h * 31 + callout.FocusKind;
                h = h * 31 + callout.PlanetId;
                h = h * 31 + callout.HasWaypoint;
                h = h * 31 + (int)(callout.WaypointX * 10f);
                h = h * 31 + (int)(callout.WaypointZ * 10f);
                return h;
            }
        }

        static void DrawNodes(
            in ShipCommsInbox.Callout callout, in ParsedWords words, Color color, float alpha, int pathCount)
        {
            Color c = color;
            c.a = alpha * 0.85f;
            if (TryLiveMe(in callout, out Vector3 me)
                && (words.HasMe || SpeakerOnPath(me, pathCount)))
            {
                DrawHollowRing(Lift(me), HullRadius(callout.NetworkId), c, callout.NetworkId, RingThicknessPixels);
            }

            if (TryLiveYou(in callout, out Vector3 you))
                DrawHollowRing(Lift(you), HullRadius(callout.YouNetworkId), c, callout.YouNetworkId, RingThicknessPixels);

            int group = 0;
            if (words.HasEveryone || words.HasTeam)
                group = ReadLiveEveryone(in callout, s_ExpandA, s_RadiusA);
            else if (words.HasEscort)
                group = ReadLiveEscort(in callout, s_ExpandA, s_RadiusA);
            else if (words.HasUs)
                group = ReadLiveUs(in callout, s_ExpandA, s_RadiusA);
            for (int i = 0; i < group; i++)
            {
                int owner = i < s_IdScratch.Length ? s_IdScratch[i] : 0;
                DrawHollowRing(Lift(s_ExpandA[i]), s_RadiusA[i], c, owner > 0 ? owner : callout.NetworkId, RingThicknessPixels);
            }

            int worldN = s_LastNodeCount;
            for (int i = 0; i < worldN; i++)
            {
                int anchor = s_AnchorScratch[i];
                if (anchor == AnchorId.Me || anchor == AnchorId.You
                    || anchor == AnchorId.Us || anchor == AnchorId.Everyone)
                    continue;
                int hits = ExpandAnchor(in callout, in words, anchor, s_ExpandA, s_RadiusA);
                for (int h = 0; h < hits; h++)
                    DrawHollowRing(Lift(s_ExpandA[h]), s_RadiusA[h], c, callout.NetworkId, RingThicknessPixels);
            }
        }

        static bool SpeakerOnPath(Vector3 speaker, int pathCount)
        {
            const float nearSq = 0.45f * 0.45f;
            for (int i = 0; i < pathCount; i++)
            {
                Vector3 a = s_PathFrom[i] - speaker;
                Vector3 b = s_PathTo[i] - speaker;
                a.y = 0f;
                b.y = 0f;
                if (a.sqrMagnitude <= nearSq || b.sqrMagnitude <= nearSq)
                    return true;
            }

            return false;
        }

        static Vector3 ResolveAim(int speakerId)
        {
            if (ShipCommsClientState.HasLastPlayAim)
                return ShipCommsClientState.LastPlayAim;
            if (TryHullPos(speakerId, out Vector3 me))
                return me;
            return Vector3.zero;
        }

        static TeamId ReadSpeakerTeam(int networkId)
        {
            if (!ShipWeaponProxyRegistry.TryGetHull(networkId, out Transform hull) || hull == null)
                return ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);
            TeamId team = ShipWeaponProxyRegistry.ReadPresentationTeam(hull);
            if (team == TeamId.None)
                team = ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);
            return team;
        }

        static int CollectClosest(
            Vector3 aim, float range, int exclude, TeamId team,
            bool teammatesOnly, bool enemiesOnly, int[] dst, int max,
            bool troopCarriersOnly = false)
        {
            return ShipWeaponProxyRegistry.CollectClosestHulls(
                aim, range, exclude, team, teammatesOnly, enemiesOnly, dst, max,
                troopCarriersOnly);
        }

        static bool TryHullPos(int networkId, out Vector3 pos)
        {
            pos = default;
            if (!ShipWeaponProxyRegistry.TryGetHull(networkId, out Transform hull) || hull == null)
                return false;
            pos = hull.position;
            return true;
        }

        static Vector3 Lift(Vector3 p)
        {
            p.y = PlayPlaneY;
            return p;
        }

        static float HullRadius(int networkId)
        {
            if (networkId > 0
                && ShipWeaponProxyRegistry.TryGetCachedHullClearance(networkId, out _, out float xz)
                && xz > 0.05f)
                return xz;
            return NodeRadius;
        }

        static float VisualRingRadius(float colliderRadius)
        {
            float r = Mathf.Max(0.05f, colliderRadius);
            if (r <= RingSmallMax)
                return Mathf.Max(RingMinRadius, r * RingSmallScale + RingSmallPad);
            return r * RingLargeScale + RingLargePad;
        }

        static void DrawHollowRing(Vector3 pos, float radius, Color color, int ownerNetworkId, float thicknessPixels)
        {
            _ = ownerNetworkId;
            Draw.Ring(pos, Vector3.up, VisualRingRadius(radius), thicknessPixels, color);
        }

        static bool TryTrimToRings(
            Vector3 from, Vector3 to, float r0, float r1, out Vector3 a, out Vector3 b)
        {
            a = from;
            b = to;
            Vector3 delta = to - from;
            float len = delta.magnitude;
            r0 = Mathf.Max(0f, r0);
            r1 = Mathf.Max(0f, r1);
            if (len <= r0 + r1 + 0.08f)
                return false;

            Vector3 dir = delta / len;
            a = from + dir * r0;
            b = to - dir * r1;
            return true;
        }

        /// <summary>
        /// Shortest-path tip so a wrap-edge mark does not stretch the long way.
        /// Map size from <see cref="ToroidalMap"/> (unset → Euclidean).
        /// </summary>
        static Vector3 UnwrapToward(Vector3 from, Vector3 dest)
        {
            Vector3 off = ToroidalMap.ShortestWorldOffsetXZ(from, dest);
            return new Vector3(from.x + off.x, dest.y, from.z + off.z);
        }

        static Color ResolveActionColor(in ShipCommsInbox.Callout callout)
        {
            ParseWords(in callout, out ParsedWords words);
            return ResolveActionColor(in words);
        }

        static Color ResolveActionColor(in ParsedWords words)
        {
            if (words.ActionColor.a > 0.01f)
                return words.ActionColor;
            if (words.ColorTeam != TeamId.None)
                return words.ColorTeam.ToColor();
            return Color.white;
        }

        static void ParseWords(in ShipCommsInbox.Callout callout, out ParsedWords words)
        {
            words = default;
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            ApplyWord(catalog, callout.K0, callout.Count >= 1, ref words);
            ApplyWord(catalog, callout.K1, callout.Count >= 2, ref words);
            ApplyWord(catalog, callout.K2, callout.Count >= 3, ref words);
            ApplyWord(catalog, callout.K3, callout.Count >= 4, ref words);
            ApplyWord(catalog, callout.K4, callout.Count >= 5, ref words);
        }

        static void ApplyWord(
            ShipCommsKeywordCatalog catalog, byte index, bool live, ref ParsedWords words)
        {
            if (!live || !catalog.TryGetLabel(index, out string label))
                return;

            Classify(label, out WordKind kind, out Color action);
            switch (kind)
            {
                case WordKind.Me: words.HasMe = true; break;
                case WordKind.You: words.HasYou = true; break;
                case WordKind.Us: words.HasUs = true; break;
                case WordKind.Escort: words.HasEscort = true; break;
                case WordKind.Everyone: words.HasEveryone = true; break;
                case WordKind.Team: words.HasTeam = true; break;
                case WordKind.Them:
                    words.HasThem = true;
                    words.Hostile = true;
                    break;
                case WordKind.Enemy:
                    words.HasEnemy = true;
                    words.Hostile = true;
                    break;
                case WordKind.Ally:
                    words.HasAlly = true;
                    words.Hostile = true;
                    break;
                case WordKind.Ship: words.HasShip = true; break;
                case WordKind.Here: words.HasHere = true; break;
                case WordKind.Planet: words.HasPlanet = true; break;
                case WordKind.Moon: words.HasMoon = true; break;
                case WordKind.Home: words.HasHome = true; break;
                case WordKind.Pad: words.HasPad = true; break;
                case WordKind.Turret: words.HasTurret = true; break;
                case WordKind.Asteroid: words.HasAsteroid = true; break;
                case WordKind.Gems: words.HasGems = true; break;
                case WordKind.Color:
                    if (TeamIdExtensions.TryParseColorName(label, out TeamId team))
                        words.ColorTeam = team;
                    break;
                case WordKind.Action:
                    words.HasAction = true;
                    words.ActionColor = action;
                    words.Converge |= IsConvergeLabel(label);
                    if (IsHostileVerb(label))
                        words.Hostile = true;
                    if (IsFriendlyVerb(label))
                        words.Friendly = true;
                    break;
            }
        }

        static void Classify(string label, out WordKind kind, out Color action)
        {
            kind = WordKind.None;
            action = default;
            if (string.IsNullOrEmpty(label))
                return;

            if (Eq(label, "Me")) { kind = WordKind.Me; return; }
            if (Eq(label, "You")) { kind = WordKind.You; return; }
            if (Eq(label, "Us")) { kind = WordKind.Us; return; }
            if (Eq(label, "Escort") || Eq(label, "Wing")) { kind = WordKind.Escort; return; }
            if (Eq(label, "Everyone")) { kind = WordKind.Everyone; return; }
            if (Eq(label, "Team")) { kind = WordKind.Team; return; }
            if (Eq(label, "Them")) { kind = WordKind.Them; return; }
            if (Eq(label, "Enemy")) { kind = WordKind.Enemy; return; }
            if (Eq(label, "Ally")) { kind = WordKind.Ally; return; }
            if (Eq(label, "Ship")) { kind = WordKind.Ship; return; }
            if (Eq(label, "Here")) { kind = WordKind.Here; return; }
            if (Eq(label, "Planet")) { kind = WordKind.Planet; return; }
            if (Eq(label, "Moon")) { kind = WordKind.Moon; return; }
            if (Eq(label, "Pad")) { kind = WordKind.Pad; return; }
            if (Eq(label, "Turret")) { kind = WordKind.Turret; return; }
            if (Eq(label, "Base") || Eq(label, "Home"))
            {
                kind = WordKind.Home;
                return;
            }

            if (Eq(label, "Asteroid")) { kind = WordKind.Asteroid; return; }
            if (Eq(label, "Gems")) { kind = WordKind.Gems; return; }
            if (TeamIdExtensions.TryParseColorName(label, out _))
            {
                kind = WordKind.Color;
                return;
            }

            if (TryActionColor(label, out action))
                kind = WordKind.Action;
        }

        static int KindToAnchor(WordKind kind)
        {
            switch (kind)
            {
                case WordKind.Me: return AnchorId.Me;
                case WordKind.You:
                case WordKind.Ship:
                case WordKind.Ally:
                case WordKind.Enemy:
                    return AnchorId.You;
                case WordKind.Them:
                case WordKind.Us:
                case WordKind.Escort:
                    return AnchorId.Us;
                case WordKind.Everyone:
                case WordKind.Team:
                    return AnchorId.Everyone;
                case WordKind.Here: return AnchorId.Here;
                case WordKind.Planet:
                case WordKind.Home:
                    return AnchorId.Planet;
                case WordKind.Moon:
                    return AnchorId.Moon;
                case WordKind.Pad:
                    return AnchorId.Pad;
                case WordKind.Turret:
                    return AnchorId.Turret;
                case WordKind.Asteroid:
                    return AnchorId.Asteroid;
                case WordKind.Gems:
                    return AnchorId.Gems;
                default: return 0;
            }
        }

        static bool IsConvergeLabel(string label)
        {
            return Eq(label, "Mining") || Eq(label, "Attack") || Eq(label, "Kill")
                || Eq(label, "Capture") || Eq(label, "Dock") || Eq(label, "Deposit")
                || Eq(label, "Transport")
                || Eq(label, "Incoming") || Eq(label, "Defend");
        }

        static bool IsHostileVerb(string label)
        {
            return Eq(label, "Attack") || Eq(label, "Kill") || Eq(label, "Push")
                || Eq(label, "Incoming") || Eq(label, "Capture");
        }

        static bool IsFriendlyVerb(string label)
        {
            return Eq(label, "Defend") || Eq(label, "Hold") || Eq(label, "Help") || Eq(label, "Heal");
        }

        static bool TryActionColor(string label, out Color color)
        {
            color = default;
            if (Eq(label, "Attack")) { color = new Color(0.92f, 0.18f, 0.16f); return true; }
            if (Eq(label, "Defend")) { color = new Color(0.22f, 0.45f, 0.95f); return true; }
            if (Eq(label, "Wait")) { color = new Color(0.70f, 0.72f, 0.78f); return true; }
            if (Eq(label, "Help")) { color = new Color(0.18f, 0.88f, 0.95f); return true; }
            if (Eq(label, "Follow")) { color = new Color(0.55f, 0.82f, 1f); return true; }
            if (Eq(label, "Retreat")) { color = new Color(0.65f, 0.40f, 0.78f); return true; }
            if (Eq(label, "Hold")) { color = new Color(0.16f, 0.30f, 0.72f); return true; }
            if (Eq(label, "Push")) { color = new Color(1f, 0.34f, 0.12f); return true; }
            if (Eq(label, "Incoming")) { color = new Color(1f, 0.42f, 0.10f); return true; }
            if (Eq(label, "Heal")) { color = new Color(0.20f, 0.85f, 0.32f); return true; }
            if (Eq(label, "Capture")) { color = new Color(0.92f, 0.28f, 0.62f); return true; }
            if (Eq(label, "Kill")) { color = new Color(0.70f, 0.07f, 0.12f); return true; }
            if (Eq(label, "Mining")) { color = new Color(0.95f, 0.55f, 0.12f); return true; }
            if (Eq(label, "Transport")) { color = new Color(0.38f, 0.72f, 0.98f); return true; }
            if (Eq(label, "Dock") || Eq(label, "Deposit")) { color = new Color(0.95f, 0.78f, 0.22f); return true; }
            return false;
        }

        static bool Eq(string label, string word)
        {
            return string.Equals(label, word, System.StringComparison.OrdinalIgnoreCase);
        }

        enum WordKind : byte
        {
            None = 0,
            Me,
            You,
            Us,
            Escort,
            Everyone,
            Team,
            Them,
            Enemy,
            Ally,
            Ship,
            Here,
            Planet,
            Moon,
            Home,
            Pad,
            Turret,
            Asteroid,
            Gems,
            Color,
            Action,
        }

        static class AnchorId
        {
            public const int Me = 1;
            public const int You = 2;
            public const int Us = 3;
            public const int Everyone = 4;
            public const int Here = 5;
            public const int Planet = 6;
            public const int Asteroid = 7;
            public const int Moon = 8;
            public const int Pad = 9;
            public const int Turret = 10;
            public const int Gems = 11;
        }

        struct ParsedWords
        {
            public bool HasMe;
            public bool HasYou;
            public bool HasUs;
            public bool HasEscort;
            public bool HasEveryone;
            public bool HasTeam;
            public bool HasThem;
            public bool HasEnemy;
            public bool HasAlly;
            public bool HasShip;
            public bool HasHere;
            public bool HasPlanet;
            public bool HasMoon;
            public bool HasHome;
            public bool HasPad;
            public bool HasTurret;
            public bool HasAsteroid;
            public bool HasGems;
            public bool HasAction;
            public bool Converge;
            public bool Hostile;
            public bool Friendly;
            public TeamId ColorTeam;
            public Color ActionColor;
        }
    }
}
