using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Resolves hold-S keyword sentences into world waypoints and paints a stadium
    /// wave of stationary dots along those paths. Sender locks mouse-dependent
    /// targets (You / Us / closest planet) onto the RPC so every viewer matches.
    /// <para>
    /// Word order is the path direction: "Me Planet" waves Me → planet; "Planet Me"
    /// waves the other way. Group words (Us / Everyone) fan to the next node.
        /// Ten recycled discs hop along 1-unit seats; they fade, they do not slide.
    /// Map size from <see cref="ToroidalMap"/>.
    /// </para>
    /// Client presentation only — no ECS gathers.
    /// </summary>
    public static class ShipCommsCalloutGraphics
    {
        public const float YouSelectRange = 120f;
        public const int MaxUsLocks = 8;
        public const int MaxEveryone = 8;

        /// <summary>Seconds for the wave front to travel start → end (matches chip life).</summary>
        const float TravelSeconds = 4f;

        /// <summary>Recycled discs — only this many are drawn; each hops to the next 1-unit seat.</summary>
        const int RecycleDots = 10;

        /// <summary>World-unit gap between seats. Map size from <see cref="ToroidalMap"/>.</summary>
        const float StepUnits = 1f;

        const int MaxStations = 200;
        const int MaxPaths = 8;
        const int MaxPts = 6;
        const float DotRadius = 0.22f;
        const float NodeRadius = 0.34f;
        const float PlayPlaneY = 0.08f;

        static readonly int[] s_IdScratch = new int[MaxEveryone];
        static readonly int[] s_AnchorScratch = new int[8];
        static readonly Vector3[] s_PosScratch = new Vector3[MaxEveryone];
        static readonly Vector3[] s_ExpandA = new Vector3[MaxEveryone];
        static readonly Vector3[] s_ExpandB = new Vector3[MaxEveryone];
        static readonly Vector3[] s_PathFrom = new Vector3[MaxPaths];
        static readonly Vector3[] s_PathTo = new Vector3[MaxPaths];
        static readonly Vector3[] s_EvalPts = new Vector3[MaxPts];
        static readonly float[] s_LensScratch = new float[MaxPts];

        /// <summary>
        /// Fills sender-resolved locks on <paramref name="callout"/> from the live
        /// sentence plus the last play-plane aim. One-shot on send.
        /// </summary>
        public static void BindResolvedTargets(ref ShipCommsInbox.Callout callout)
        {
            ParseWords(in callout, out ParsedWords words);
            Vector3 aim = ResolveAim(callout.NetworkId);
            TeamId speakerTeam = ReadSpeakerTeam(callout.NetworkId);
            bool keepMapPing = callout.HasWaypoint != 0
                && (callout.FocusKind == ShipCommsInbox.FocusKind.MapPing
                    || callout.FocusKind == ShipCommsInbox.FocusKind.None);

            if (words.HasYou || words.HasShip || words.HasAlly)
            {
                int locked = ShipCommsClientState.HasPendingYou
                    ? ShipCommsClientState.PendingYouNetworkId
                    : 0;
                if (locked <= 0)
                    TryResolveYou(aim, callout.NetworkId, out locked);
                callout.YouNetworkId = locked;
            }

            if (words.HasThem || words.HasEnemy)
            {
                int them = CollectClosest(
                    aim, YouSelectRange, callout.NetworkId, speakerTeam,
                    teammatesOnly: false, enemiesOnly: true, s_IdScratch, 1);
                if (them > 0 && callout.YouNetworkId <= 0)
                    callout.YouNetworkId = s_IdScratch[0];
            }

            callout.Everyone = words.HasEveryone || words.HasTeam ? (byte)1 : (byte)0;

            if (keepMapPing)
            {
                callout.FocusKind = ShipCommsInbox.FocusKind.MapPing;
            }
            else if (words.HasHere)
            {
                callout.HasWaypoint = 1;
                callout.WaypointX = aim.x;
                callout.WaypointZ = aim.z;
                callout.FocusKind = ShipCommsInbox.FocusKind.MapPing;
            }
            else
            {
                var viz = EcsWorldVisualizer.Active;
                if ((words.HasAsteroid || words.HasGems)
                    && viz != null
                    && viz.TryFindClosestAsteroid(aim, words.ColorTeam, out Vector3 rock))
                {
                    callout.HasWaypoint = 1;
                    callout.WaypointX = rock.x;
                    callout.WaypointZ = rock.z;
                    callout.FocusKind = ShipCommsInbox.FocusKind.Asteroid;
                }
                else if (words.HasPlanet || words.HasMoon || words.HasHome)
                {
                    TeamId planetTeam = words.ColorTeam;
                    if (words.HasHome && planetTeam == TeamId.None)
                        planetTeam = speakerTeam;
                    if (viz != null
                        && viz.TryFindClosestPlanet(
                            aim, planetTeam, words.HasHome, out int planetId, out Vector3 planetPos))
                    {
                        callout.PlanetId = planetId;
                        callout.HasWaypoint = 1;
                        callout.WaypointX = planetPos.x;
                        callout.WaypointZ = planetPos.z;
                        callout.FocusKind = ShipCommsInbox.FocusKind.Planet;
                    }
                }
            }

            SnapshotFrozen(ref callout, in words, speakerTeam, aim);
        }

        /// <summary>
        /// Writes speaker / You / Us seats at send time. Playback uses these only —
        /// live hulls may have moved or the rock may have died later.
        /// </summary>
        static void SnapshotFrozen(
            ref ShipCommsInbox.Callout callout,
            in ParsedWords words,
            TeamId speakerTeam,
            Vector3 aim)
        {
            if (TryHullPos(callout.NetworkId, out Vector3 me))
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
            else if (words.HasUs)
            {
                if (TryHullPos(callout.NetworkId, out me))
                {
                    s_PosScratch[0] = me;
                    n = 1;
                }

                int extra = CollectClosest(
                    aim, YouSelectRange, callout.NetworkId, speakerTeam,
                    teammatesOnly: true, enemiesOnly: false, s_IdScratch, MaxUsLocks - n);
                for (int i = 0; i < extra && n < MaxEveryone; i++)
                {
                    if (!TryHullPos(s_IdScratch[i], out Vector3 mate))
                        continue;
                    s_PosScratch[n++] = mate;
                }
            }

            callout.GroupCount = (byte)n;
            for (int i = 0; i < MaxEveryone; i++)
            {
                Vector3 p = i < n ? s_PosScratch[i] : Vector3.zero;
                SetGroup(ref callout, i, p);
            }
        }

        /// <summary>Closest ship to <paramref name="aim"/> within <see cref="YouSelectRange"/>.</summary>
        public static bool TryResolveYou(Vector3 aim, int excludeNetworkId, out int networkId)
        {
            return ShipWeaponProxyRegistry.TryGetClosestHull(
                aim, YouSelectRange, excludeNetworkId, out networkId, out _);
        }

        /// <summary>
        /// True when this label is a world anchor (Me / You / Planet / …).
        /// Used by the compose panel to lock targets on click.
        /// </summary>
        public static bool IsAnchorLabel(string label)
        {
            Classify(label, out WordKind kind, out _);
            return kind != WordKind.None && kind != WordKind.Action && kind != WordKind.Color;
        }

        /// <summary>
        /// Stadium-wave dots + waypoint discs for one callout. Must run inside an
        /// existing Shapes <c>Draw.Command</c>. Follows live hulls; ping / asteroid stay frozen.
        /// </summary>
        public static void DrawIntent(in ShipCommsInbox.Callout callout, float age, float lifetime, float alpha)
        {
            if (callout.Count < 1)
                return;

            Color color = ResolveActionColor(in callout);

            int pathCount = BuildPaths(in callout, s_PathFrom, s_PathTo, MaxPaths);
            Draw.ThicknessSpace = ThicknessSpace.Meters;

            for (int p = 0; p < pathCount; p++)
            {
                s_EvalPts[0] = Lift(s_PathFrom[p]);
                s_EvalPts[1] = Lift(UnwrapToward(s_PathFrom[p], s_PathTo[p]));
                DrawPathDots(s_EvalPts, 2, age, lifetime, color);
            }

            DrawNodes(in callout, color, alpha);
            Draw.ThicknessSpace = ThicknessSpace.Pixels;
        }

        /// <summary>Compose-time pointer on a locked "You" hull.</summary>
        public static void DrawPendingYou(float alpha)
        {
            if (!ShipCommsClientState.HasPendingYou)
                return;
            if (!TryHullPos(ShipCommsClientState.PendingYouNetworkId, out Vector3 pos))
                return;

            Color c = new Color(0.35f, 0.72f, 0.95f, 0.85f * alpha);
            Draw.ThicknessSpace = ThicknessSpace.Meters;
            Draw.Disc(Lift(pos), Vector3.up, NodeRadius, c);
            Draw.ThicknessSpace = ThicknessSpace.Pixels;
        }

        static void DrawPathDots(Vector3[] pts, int count, float age, float lifetime, Color color)
        {
            if (count < 2)
                return;

            float length = 0f;
            for (int i = 0; i < count - 1; i++)
                length += Vector3.Distance(pts[i], pts[i + 1]);
            if (length < 0.15f)
                return;

            // One seat per world unit. A disc stays on its seat through fade-out;
            // only then is that slot reused on the next front seat.
            int stations = Mathf.Clamp(Mathf.RoundToInt(length / StepUnits) + 1, 2, MaxStations);
            float life = Mathf.Max(0.35f, Mathf.Min(TravelSeconds, lifetime));
            const float fadeSteps = 1f;
            // Front travels stations-1 seats, then the remaining train needs
            // RecycleDots + fadeSteps more so every leftover disc can fade out
            // before the callout dies.
            float denom = (stations - 1) + RecycleDots + fadeSteps;
            float wave = (age / life) * denom;

            int i0 = Mathf.Max(0, Mathf.CeilToInt(wave - RecycleDots - fadeSteps));
            int i1 = Mathf.Min(stations - 1, Mathf.FloorToInt(wave));
            for (int i = i0; i <= i1; i++)
            {
                float phase = wave - i;
                if (phase < 0f)
                    continue;

                float envelope = RecycleFade(phase, RecycleDots, fadeSteps);
                if (envelope < 0.02f)
                    continue;

                float t = i / (float)(stations - 1);
                Color c = color;
                c.a = envelope;
                Draw.Disc(EvaluatePoly(pts, count, t), Vector3.up, DotRadius, c);
            }
        }

        /// <summary>
        /// Stadium seat: fade in as the front arrives, hold, then fade out. The disc
        /// is not dropped until the out envelope hits 0 — that is when it may hop
        /// to the next 1-unit seat. After the front reaches the last station the
        /// remaining train keeps aging through this same envelope so every disc
        /// finishes fading out.
        /// </summary>
        static float RecycleFade(float phase, float holdWindow, float fadeSteps)
        {
            fadeSteps = Mathf.Max(0.05f, fadeSteps);
            if (phase < fadeSteps)
                return Smooth01(phase / fadeSteps);
            if (phase <= holdWindow)
                return 1f;
            float outEnd = holdWindow + fadeSteps;
            if (phase < outEnd)
                return Smooth01((outEnd - phase) / fadeSteps);
            return 0f;
        }

        static float Smooth01(float u)
        {
            u = Mathf.Clamp01(u);
            return u * u * (3f - 2f * u);
        }

        static Vector3 EvaluatePoly(Vector3[] pts, int count, float t)
        {
            if (count <= 1)
                return pts[0];
            if (t <= 0f)
                return pts[0];
            if (t >= 1f)
                return pts[count - 1];

            float acc = 0f;
            int segs = count - 1;
            for (int i = 0; i < segs; i++)
            {
                s_LensScratch[i] = Vector3.Distance(pts[i], pts[i + 1]);
                acc += s_LensScratch[i];
            }

            if (acc < 0.001f)
                return pts[0];

            float remain = t * acc;
            for (int i = 0; i < segs; i++)
            {
                if (remain > s_LensScratch[i] && i < segs - 1)
                {
                    remain -= s_LensScratch[i];
                    continue;
                }

                float u = s_LensScratch[i] > 0.0001f ? remain / s_LensScratch[i] : 1f;
                return Vector3.Lerp(pts[i], pts[i + 1], u);
            }

            return pts[count - 1];
        }

        static int BuildPaths(
            in ShipCommsInbox.Callout callout,
            Vector3[] from,
            Vector3[] to,
            int maxPaths)
        {
            ParseWords(in callout, out ParsedWords words);
            int nodeCount = CollectOrderedAnchors(in callout, in words, s_AnchorScratch);
            if (nodeCount <= 0)
                return 0;

            int written = 0;

            // Word order is direction: first anchor → next. "Planet Me" is planet → Me.
            for (int i = 0; i < nodeCount - 1 && written < maxPaths; i++)
            {
                int fromN = ExpandAnchor(in callout, in words, s_AnchorScratch[i], s_ExpandA);
                int toN = ExpandAnchor(in callout, in words, s_AnchorScratch[i + 1], s_ExpandB);
                if (fromN <= 0 || toN <= 0)
                    continue;

                if (fromN == 1 && toN == 1)
                {
                    from[written] = s_ExpandA[0];
                    to[written] = s_ExpandB[0];
                    written++;
                    continue;
                }

                if (fromN > 1 && toN == 1)
                {
                    for (int a = 0; a < fromN && written < maxPaths; a++)
                    {
                        from[written] = s_ExpandA[a];
                        to[written] = s_ExpandB[0];
                        written++;
                    }

                    continue;
                }

                if (fromN == 1 && toN > 1)
                {
                    for (int b = 0; b < toN && written < maxPaths; b++)
                    {
                        from[written] = s_ExpandA[0];
                        to[written] = s_ExpandB[b];
                        written++;
                    }

                    continue;
                }

                for (int a = 0; a < fromN && written < maxPaths; a++)
                {
                    from[written] = s_ExpandA[a];
                    to[written] = s_ExpandB[a % toN];
                    written++;
                }
            }

            // Lone focus + an action still gets Me → target so a solo "Mining Asteroid" reads.
            if (written == 0 && TryFrozenMe(in callout, out Vector3 speaker))
            {
                if (TryFocusPos(in callout, in words, out Vector3 lone))
                {
                    from[0] = speaker;
                    to[0] = lone;
                    written = 1;
                }
                else if (TryFrozenYou(in callout, out Vector3 you))
                {
                    from[0] = speaker;
                    to[0] = you;
                    written = 1;
                }
            }

            return written;
        }

        /// <summary>
        /// Ordered anchor kinds as bytes (see <see cref="AnchorId"/>). Color / action
        /// words are skipped so "Me Heal You" is Me then You.
        /// </summary>
        static int CollectOrderedAnchors(
            in ShipCommsInbox.Callout callout,
            in ParsedWords words,
            int[] dst)
        {
            int n = 0;
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            AppendAnchor(catalog, callout.K0, callout.Count >= 1, dst, ref n);
            AppendAnchor(catalog, callout.K1, callout.Count >= 2, dst, ref n);
            AppendAnchor(catalog, callout.K2, callout.Count >= 3, dst, ref n);
            AppendAnchor(catalog, callout.K3, callout.Count >= 4, dst, ref n);
            AppendAnchor(catalog, callout.K4, callout.Count >= 5, dst, ref n);

            if (callout.HasWaypoint != 0 || callout.FocusKind == ShipCommsInbox.FocusKind.Planet)
            {
                bool already = false;
                for (int i = 0; i < n; i++)
                {
                    if (dst[i] == AnchorId.Here || dst[i] == AnchorId.Asteroid
                        || dst[i] == AnchorId.Planet)
                    {
                        already = true;
                        break;
                    }
                }

                if (!already && n < dst.Length)
                {
                    if (callout.FocusKind == ShipCommsInbox.FocusKind.Planet)
                        dst[n++] = AnchorId.Planet;
                    else if (callout.FocusKind == ShipCommsInbox.FocusKind.Asteroid)
                        dst[n++] = AnchorId.Asteroid;
                    else
                        dst[n++] = AnchorId.Here;
                }
            }

            _ = words;
            return n;
        }

        static void AppendAnchor(
            ShipCommsKeywordCatalog catalog, byte index, bool live, int[] dst, ref int n)
        {
            if (!live || n >= dst.Length)
                return;
            if (!catalog.TryGetLabel(index, out string label))
                return;
            Classify(label, out WordKind kind, out _);
            int id = KindToAnchor(kind);
            if (id == 0)
                return;
            dst[n++] = id;
        }

        static int ExpandAnchor(
            in ShipCommsInbox.Callout callout, in ParsedWords words, int anchor, Vector3[] dst)
        {
            switch (anchor)
            {
                case AnchorId.Me:
                    return TryFrozenMe(in callout, out dst[0]) ? 1 : 0;
                case AnchorId.You:
                    return TryFrozenYou(in callout, out dst[0]) ? 1 : 0;
                case AnchorId.Us:
                case AnchorId.Everyone:
                    return ReadFrozenGroup(in callout, dst);
                case AnchorId.Here:
                case AnchorId.Asteroid:
                case AnchorId.Planet:
                    if (callout.HasWaypoint == 0)
                        return 0;
                    dst[0] = new Vector3(callout.WaypointX, 0f, callout.WaypointZ);
                    return 1;
                default:
                    _ = words;
                    return 0;
            }
        }

        static bool TryFrozenMe(in ShipCommsInbox.Callout callout, out Vector3 pos)
        {
            pos = new Vector3(callout.MeX, 0f, callout.MeZ);
            return pos.x != 0f || pos.z != 0f || TryHullPos(callout.NetworkId, out pos);
        }

        static bool TryFrozenYou(in ShipCommsInbox.Callout callout, out Vector3 pos)
        {
            pos = new Vector3(callout.YouX, 0f, callout.YouZ);
            return pos.x != 0f || pos.z != 0f;
        }

        static int ReadFrozenGroup(in ShipCommsInbox.Callout callout, Vector3[] dst)
        {
            int n = Mathf.Min(callout.GroupCount, dst.Length);
            int written = 0;
            for (int i = 0; i < n; i++)
            {
                if (!TryGetGroup(in callout, i, out dst[written]))
                    continue;
                written++;
            }

            return written;
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

        static bool TryFocusPos(
            in ShipCommsInbox.Callout callout, in ParsedWords words, out Vector3 pos)
        {
            pos = default;
            if (callout.HasWaypoint != 0)
            {
                pos = new Vector3(callout.WaypointX, 0f, callout.WaypointZ);
                return true;
            }

            _ = words;
            return false;
        }

        static void DrawNodes(in ShipCommsInbox.Callout callout, Color color, float alpha)
        {
            ParseWords(in callout, out ParsedWords words);
            Color c = color;
            c.a = alpha * 0.7f;
            if (words.HasMe && TryFrozenMe(in callout, out Vector3 me))
                Draw.Disc(Lift(me), Vector3.up, NodeRadius * 0.55f, c);
            if (TryFrozenYou(in callout, out Vector3 you))
                Draw.Disc(Lift(you), Vector3.up, NodeRadius, c);
            int group = ReadFrozenGroup(in callout, s_ExpandA);
            for (int i = 0; i < group; i++)
                Draw.Disc(Lift(s_ExpandA[i]), Vector3.up, NodeRadius * 0.85f, c);
            if (TryFocusPos(in callout, default, out Vector3 focus))
                Draw.Disc(Lift(focus), Vector3.up, NodeRadius, c);
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
            bool teammatesOnly, bool enemiesOnly, int[] dst, int max)
        {
            return ShipWeaponProxyRegistry.CollectClosestHulls(
                aim, range, exclude, team, teammatesOnly, enemiesOnly, dst, max);
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
                case WordKind.Everyone: words.HasEveryone = true; break;
                case WordKind.Team: words.HasTeam = true; break;
                case WordKind.Them: words.HasThem = true; break;
                case WordKind.Enemy: words.HasEnemy = true; break;
                case WordKind.Ally: words.HasAlly = true; break;
                case WordKind.Ship: words.HasShip = true; break;
                case WordKind.Here: words.HasHere = true; break;
                case WordKind.Planet: words.HasPlanet = true; break;
                case WordKind.Moon: words.HasMoon = true; break;
                case WordKind.Home: words.HasHome = true; break;
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
            if (Eq(label, "Everyone")) { kind = WordKind.Everyone; return; }
            if (Eq(label, "Team")) { kind = WordKind.Team; return; }
            if (Eq(label, "Them")) { kind = WordKind.Them; return; }
            if (Eq(label, "Enemy")) { kind = WordKind.Enemy; return; }
            if (Eq(label, "Ally")) { kind = WordKind.Ally; return; }
            if (Eq(label, "Ship")) { kind = WordKind.Ship; return; }
            if (Eq(label, "Here")) { kind = WordKind.Here; return; }
            if (Eq(label, "Planet")) { kind = WordKind.Planet; return; }
            if (Eq(label, "Moon")) { kind = WordKind.Moon; return; }
            if (Eq(label, "Base") || Eq(label, "Home") || Eq(label, "Pad") || Eq(label, "Dock"))
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
                case WordKind.Them:
                case WordKind.Enemy:
                    return AnchorId.You;
                case WordKind.Us: return AnchorId.Us;
                case WordKind.Everyone:
                case WordKind.Team:
                    return AnchorId.Everyone;
                case WordKind.Here: return AnchorId.Here;
                case WordKind.Planet:
                case WordKind.Moon:
                case WordKind.Home:
                    return AnchorId.Planet;
                case WordKind.Asteroid:
                case WordKind.Gems:
                    return AnchorId.Asteroid;
                default: return 0;
            }
        }

        static bool IsConvergeLabel(string label)
        {
            return Eq(label, "Mining") || Eq(label, "Attack") || Eq(label, "Kill")
                || Eq(label, "Capture") || Eq(label, "Rally") || Eq(label, "Scout")
                || Eq(label, "Incoming") || Eq(label, "Defend");
        }

        static bool TryActionColor(string label, out Color color)
        {
            color = default;
            if (Eq(label, "Heal")) { color = new Color(0.20f, 0.85f, 0.32f); return true; }
            if (Eq(label, "Mining")) { color = new Color(0.95f, 0.55f, 0.12f); return true; }
            if (Eq(label, "Attack") || Eq(label, "Kill") || Eq(label, "Push"))
            {
                color = new Color(0.92f, 0.18f, 0.16f);
                return true;
            }

            if (Eq(label, "Defend") || Eq(label, "Hold"))
            {
                color = new Color(0.22f, 0.45f, 0.95f);
                return true;
            }

            if (Eq(label, "Help")) { color = new Color(0.25f, 0.85f, 0.95f); return true; }
            if (Eq(label, "Follow") || Eq(label, "Scout"))
            {
                color = new Color(0.70f, 0.90f, 1f);
                return true;
            }

            if (Eq(label, "Capture")) { color = new Color(0.95f, 0.55f, 0.12f); return true; }
            if (Eq(label, "Retreat")) { color = new Color(0.65f, 0.45f, 0.70f); return true; }
            if (Eq(label, "Rally")) { color = new Color(0.95f, 0.85f, 0.40f); return true; }
            if (Eq(label, "Incoming")) { color = new Color(0.95f, 0.50f, 0.15f); return true; }
            if (Eq(label, "Wait")) { color = new Color(0.75f, 0.75f, 0.80f); return true; }
            if (Eq(label, "Transport")) { color = new Color(0.45f, 0.75f, 0.95f); return true; }
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
        }

        struct ParsedWords
        {
            public bool HasMe;
            public bool HasYou;
            public bool HasUs;
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
            public bool HasAsteroid;
            public bool HasGems;
            public bool HasAction;
            public bool Converge;
            public TeamId ColorTeam;
            public Color ActionColor;
        }
    }
}
