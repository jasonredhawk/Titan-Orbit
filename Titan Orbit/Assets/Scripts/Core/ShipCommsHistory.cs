using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Client-only ring of recent sentences this player sent from the hold-S comms matrix.
    /// <c>ShipCommsPanel</c> paints them as quick-select chips on the right rail. Not
    /// networked — each machine only remembers what <b>this</b> player composed.
    /// <para>
    /// [UNITY] PlayerPrefs (a tiny local key/value store) keeps the list across matches
    /// and app restarts. Domain Reload clears the in-memory list, then the next read
    /// reloads the same saved string. MPPM (Multiplayer Play Mode) clones share one
    /// registry — the compose panel suffixes the key so Player 2 does not overwrite
    /// Player 1.
    /// </para>
    /// </summary>
    public static class ShipCommsHistory
    {
        /// <summary>
        /// [UNITY] PlayerPrefs key for the packed recent-sentence string. A missing key
        /// means a first-time player with an empty RECENT column.
        /// </summary>
        public const string HistoryPrefsKey = "TitanOrbit.ShipComms.History";

        /// <summary>
        /// Upper bound so a future taller matrix cannot spawn an unbounded chip list.
        /// The compose panel binds a smaller capacity that fills the card.
        /// </summary>
        public const int HardMaxEntries = 48;

        /// <summary>
        /// Fallback slot count until the compose panel measures the card and calls
        /// <see cref="BindCapacity"/>. Ten matches the original RECENT rail.
        /// </summary>
        public const int DefaultEntries = 10;

        /// <summary>Packed-string version. Bump this if the line format changes.</summary>
        const int PersistVersion = 1;

        /// <summary>One 1–5 keyword sentence stored as catalog indices.</summary>
        public struct Sentence
        {
            /// <summary>Live keyword count (1–5).</summary>
            public byte Count;

            /// <summary>First keyword index.</summary>
            public byte K0;

            /// <summary>Second keyword index. Ignored when <see cref="Count"/> is 1.</summary>
            public byte K1;

            /// <summary>Third keyword index. Ignored when <see cref="Count"/> is under 3.</summary>
            public byte K2;

            /// <summary>Fourth keyword index. Ignored when <see cref="Count"/> is under 4.</summary>
            public byte K3;

            /// <summary>Fifth keyword index. Ignored when <see cref="Count"/> is under 5.</summary>
            public byte K4;

            /// <summary>1 when this sentence locked a minimap / Here world point.</summary>
            public byte HasWaypoint;

            /// <summary>World X of the remembered ping.</summary>
            public float WaypointX;

            /// <summary>World Z of the remembered ping.</summary>
            public float WaypointZ;

            /// <summary>True when both sentences use the same indices and the same ping.</summary>
            public bool EqualsSentence(in Sentence other)
            {
                if (Count != other.Count
                    || K0 != other.K0
                    || K1 != other.K1
                    || K2 != other.K2
                    || K3 != other.K3
                    || K4 != other.K4
                    || HasWaypoint != other.HasWaypoint)
                    return false;

                if (HasWaypoint == 0)
                    return true;

                return Mathf.Abs(WaypointX - other.WaypointX) < 0.05f
                    && Mathf.Abs(WaypointZ - other.WaypointZ) < 0.05f;
            }
        }

        /// <summary>Newest-first ring. Capacity follows <see cref="MaxEntries"/>.</summary>
        static readonly List<Sentence> s_Recent = new List<Sentence>(DefaultEntries);

        /// <summary>
        /// How many recent sentences the compose panel can show. The panel sets this
        /// at build time so the RECENT column fills the card.
        /// </summary>
        static int s_MaxEntries = DefaultEntries;

        /// <summary>
        /// Active PlayerPrefs key. The compose panel may swap this for an MPPM
        /// instance suffix so Player 2 does not overwrite Player 1's history.
        /// </summary>
        static string s_PrefsKey = HistoryPrefsKey;

        /// <summary>False until the first load this process (or after Domain Reload).</summary>
        static bool s_Loaded;

        /// <summary>How many RECENT rows the compose panel should build.</summary>
        public static int MaxEntries => s_MaxEntries;

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky. Drop the in-memory list and
        /// the load flag so the next Play re-reads PlayerPrefs instead of a stale ring
        /// or an empty list that would hide the saved sentences.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Recent.Clear();
            s_MaxEntries = DefaultEntries;
            s_PrefsKey = HistoryPrefsKey;
            s_Loaded = false;
        }

        /// <summary>Newest-first sentences. Count is 0–<see cref="MaxEntries"/>.</summary>
        public static IReadOnlyList<Sentence> Recent
        {
            get
            {
                EnsureLoaded();
                return s_Recent;
            }
        }

        /// <summary>
        /// Points later reads/writes at <paramref name="prefsKey"/> (MPPM per-instance
        /// keys). Call once at panel Awake before the first <see cref="Record"/>.
        /// Reloads the saved list for that key.
        /// </summary>
        /// <param name="prefsKey">PlayerPrefs key for this Editor / player instance.</param>
        public static void BindPrefsKey(string prefsKey)
        {
            if (string.IsNullOrEmpty(prefsKey))
                return;

            // --- Swap store ---
            // [TITAN-ORBIT] MPPM clones share one PlayerPrefs file. A new key means a
            // different player's history — drop the in-memory ring and reload.
            s_PrefsKey = prefsKey;
            s_Loaded = false;
            s_Recent.Clear();
            EnsureLoaded();
        }

        /// <summary>
        /// Caps the ring to <paramref name="capacity"/> slots so the panel and the
        /// saved list stay the same length. Extra oldest rows are dropped and written
        /// back to PlayerPrefs.
        /// </summary>
        /// <param name="capacity">Visible RECENT rows (1–<see cref="HardMaxEntries"/>).</param>
        public static void BindCapacity(int capacity)
        {
            EnsureLoaded();
            int next = Mathf.Clamp(capacity, 1, HardMaxEntries);
            if (next == s_MaxEntries && s_Recent.Count <= next)
                return;

            s_MaxEntries = next;
            TrimToCapacity();
            SaveToPrefs();
        }

        /// <summary>
        /// Pushes a sent sentence to the front. A duplicate is moved to slot 0 instead of
        /// growing the list. Drops the oldest when over <see cref="MaxEntries"/>, then
        /// writes PlayerPrefs so the next match still has this chip.
        /// </summary>
        public static void Record(
            byte count, byte k0, byte k1, byte k2, byte k3, byte k4,
            byte hasWaypoint = 0, float waypointX = 0f, float waypointZ = 0f)
        {
            if (count < 1)
                return;

            EnsureLoaded();

            var sentence = new Sentence
            {
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                K3 = k3,
                K4 = k4,
                HasWaypoint = hasWaypoint != 0 ? (byte)1 : (byte)0,
                WaypointX = waypointX,
                WaypointZ = waypointZ,
            };

            // --- Dedup ---
            // Re-sending "ATTACK · BASE" should bump that chip to the top, not add a twin.
            for (int i = 0; i < s_Recent.Count; i++)
            {
                if (!s_Recent[i].EqualsSentence(sentence))
                    continue;

                s_Recent.RemoveAt(i);
                break;
            }

            s_Recent.Insert(0, sentence);
            TrimToCapacity();
            SaveToPrefs();
        }

        /// <summary>Reads one history slot. Returns false when that slot is empty.</summary>
        public static bool TryGet(int index, out Sentence sentence)
        {
            EnsureLoaded();
            if (index < 0 || index >= s_Recent.Count)
            {
                sentence = default;
                return false;
            }

            sentence = s_Recent[index];
            return true;
        }

        /// <summary>Drops oldest rows when the ring is longer than <see cref="MaxEntries"/>.</summary>
        static void TrimToCapacity()
        {
            while (s_Recent.Count > s_MaxEntries)
                s_Recent.RemoveAt(s_Recent.Count - 1);
        }

        /// <summary>
        /// Reads the saved string once per process (or after <see cref="BindPrefsKey"/>).
        /// A missing or corrupt key leaves the ring empty — the player just sees blank rows.
        /// </summary>
        static void EnsureLoaded()
        {
            if (s_Loaded)
                return;

            s_Loaded = true;
            LoadFromPrefs();
        }

        /// <summary>
        /// [UNITY] PlayerPrefs.GetString of the packed history. One sentence per line
        /// after a version header. Bad lines are skipped so a partial write cannot wipe
        /// the whole column.
        /// </summary>
        static void LoadFromPrefs()
        {
            s_Recent.Clear();
            string packed = PlayerPrefs.GetString(s_PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(packed))
                return;

            // --- Split lines ---
            // [STANDARD] \n from our writer; tolerate \r\n if a human edited the key.
            string[] lines = packed.Split('\n');
            if (lines.Length < 2)
                return;

            if (!int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
                || version != PersistVersion)
                return;

            for (int i = 1; i < lines.Length && s_Recent.Count < HardMaxEntries; i++)
            {
                if (!TryParseSentence(lines[i], out Sentence sentence))
                    continue;

                s_Recent.Add(sentence);
            }

            TrimToCapacity();
        }

        /// <summary>
        /// Writes newest-first sentences as an invariant-culture string. Called after
        /// every send and after a capacity trim — not per frame.
        /// </summary>
        static void SaveToPrefs()
        {
            var sb = new StringBuilder(s_Recent.Count * 40);
            sb.Append(PersistVersion.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < s_Recent.Count; i++)
            {
                Sentence s = s_Recent[i];
                sb.Append('\n');
                sb.Append(s.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.K0.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.K1.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.K2.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.K3.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.K4.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.HasWaypoint.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.WaypointX.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(s.WaypointZ.ToString("R", CultureInfo.InvariantCulture));
            }

            PlayerPrefs.SetString(s_PrefsKey, sb.ToString());
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Parses one "count k0 k1 k2 k3 k4 hasWp x z" line. False when the row is
        /// blank, the count is out of range, or a field is not a number.
        /// </summary>
        static bool TryParseSentence(string line, out Sentence sentence)
        {
            sentence = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] parts = line.Trim().Split(' ');
            if (parts.Length < 9)
                return false;

            if (!TryParseByte(parts[0], out byte count) || count < 1 || count > 5)
                return false;
            if (!TryParseByte(parts[1], out byte k0))
                return false;
            if (!TryParseByte(parts[2], out byte k1))
                return false;
            if (!TryParseByte(parts[3], out byte k2))
                return false;
            if (!TryParseByte(parts[4], out byte k3))
                return false;
            if (!TryParseByte(parts[5], out byte k4))
                return false;
            if (!TryParseByte(parts[6], out byte hasWaypoint))
                return false;
            if (!float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float waypointX))
                return false;
            if (!float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float waypointZ))
                return false;

            sentence = new Sentence
            {
                Count = count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                K3 = k3,
                K4 = k4,
                HasWaypoint = hasWaypoint != 0 ? (byte)1 : (byte)0,
                WaypointX = waypointX,
                WaypointZ = waypointZ,
            };
            return true;
        }

        /// <summary>Parses a 0–255 field from the packed line. False on garbage text.</summary>
        static bool TryParseByte(string text, out byte value)
        {
            if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            value = 0;
            return false;
        }
    }
}
