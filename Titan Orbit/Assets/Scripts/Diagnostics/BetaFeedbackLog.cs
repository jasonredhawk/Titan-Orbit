using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Append-only text log of beta field notes. One tab-separated line per note, so a
    /// developer can open the file later without a database. SEND adds a line. Delete
    /// rewrites the file without that id — the only edit the file ever gets.
    /// <para>
    /// The file is <c>BetaFeedback.log</c> next to the build (parent of <c>Application.dataPath</c>),
    /// the same folder as <see cref="DedicatedServerFileLog"/>. In the Editor that is the
    /// Unity project folder (<c>Titan Orbit/BetaFeedback.log</c>). On the Linux dedicated
    /// server it sits beside the headless binary. Clients never write this file; they ask
    /// the server over RPC. <c>*.log</c> is already gitignored.
    /// </para>
    /// </summary>
    public static class BetaFeedbackLog
    {
        /// <summary>Stop accepting new lines once the file is this large, so a stuck client cannot fill the disk.</summary>
        public const int MaxFileBytes = 2 * 1024 * 1024;

        /// <summary>Newest rows the NOTES screen asks for. Older lines stay in the file.</summary>
        public const int MaxListed = 100;

        /// <summary>Typed note cap. Matches the input field and the NetCode fixed string.</summary>
        public const int MaxNoteLength = 400;

        const string FileName = "BetaFeedback.log";

        static readonly object Gate = new object();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        static List<BetaFeedbackEntry> s_Rows;
        static bool s_AnnouncedPath;

        /// <summary>
        /// [UNITY] Domain Reload off keeps statics between Play sessions. Drop the cache so the
        /// next Play reads the file again (including notes you deleted by hand).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlayMode()
        {
            s_Rows = null;
            s_AnnouncedPath = false;
        }

        /// <summary>
        /// Absolute path of the log. Parent of the Unity data folder, else the persistent data path.
        /// </summary>
        public static string FilePath
        {
            get
            {
                // [UNITY] dataPath is ".../Assets" in the Editor and ".../Build_Data" on a player.
                // The parent is the project folder or the dedicated-server build folder.
                string dir = null;
                try
                {
                    if (!string.IsNullOrEmpty(Application.dataPath))
                        dir = Path.GetDirectoryName(Application.dataPath);
                }
                catch (Exception)
                {
                    dir = null;
                }

                if (string.IsNullOrEmpty(dir))
                    dir = Application.persistentDataPath;
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>
        /// Loads the file once, then copies every row into <paramref name="dest"/> (oldest first).
        /// Safe to call from the server main thread when a list RPC arrives — not from a tick loop.
        /// </summary>
        /// <param name="dest">Cleared, then filled. Never null.</param>
        public static void CopyAll(List<BetaFeedbackEntry> dest)
        {
            if (dest == null)
                return;

            lock (Gate)
            {
                EnsureLoaded();
                dest.Clear();
                for (int i = 0; i < s_Rows.Count; i++)
                    dest.Add(s_Rows[i]);
            }
        }

        /// <summary>
        /// Appends one note and returns its new id (starting at 1).
        /// −1 when the file is already at <see cref="MaxFileBytes"/>. 0 when the write failed.
        /// </summary>
        /// <param name="entry">Row to store. <see cref="BetaFeedbackEntry.Id"/> is ignored and replaced.</param>
        /// <returns>Positive id, −1 if full, 0 on an IO error.</returns>
        public static int TryAppend(BetaFeedbackEntry entry)
        {
            lock (Gate)
            {
                try
                {
                    EnsureLoaded();
                    entry.Id = NextId();
                    entry.Player = CleanField(entry.Player, 24);
                    entry.Ship = CleanField(entry.Ship, 120);
                    entry.Gun = CleanField(entry.Gun, 64);
                    entry.Abilities = CleanField(entry.Abilities, 400);
                    entry.Note = CleanField(entry.Note, MaxNoteLength);

                    string path = FilePath;
                    AnnounceOnce(path);

                    if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
                        return -1;

                    // Write first. Memory is updated only after the line is on disk, so a
                    // failed write does not leave a note that vanishes on the next restart.
                    string line = Encode(entry);
                    if (!File.Exists(path))
                        File.WriteAllText(path, Header + line, Utf8);
                    else
                        File.AppendAllText(path, line, Utf8);

                    s_Rows.Add(entry);
                    return entry.Id;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[BetaFeedback] Could not append note: " + ex.Message);
                    return 0;
                }
            }
        }

        /// <summary>
        /// Removes the row with <paramref name="id"/> and rewrites the file.
        /// False when that id is not in the log.
        /// </summary>
        /// <param name="id">Id returned by <see cref="TryAppend"/>.</param>
        /// <returns>True when a line was removed.</returns>
        public static bool TryDelete(int id)
        {
            if (id <= 0)
                return false;

            lock (Gate)
            {
                try
                {
                    EnsureLoaded();
                    int found = -1;
                    for (int i = 0; i < s_Rows.Count; i++)
                    {
                        if (s_Rows[i].Id != id)
                            continue;
                        found = i;
                        break;
                    }

                    if (found < 0)
                        return false;

                    s_Rows.RemoveAt(found);
                    Rewrite();
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[BetaFeedback] Could not delete note " + id + ": " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>First-line comment so a human opening the file knows the columns.</summary>
        const string Header = "# id\tplayer\tship\tgun\tshipLevel\tbranch\tabilities\tnote\n";

        /// <summary>Reads the file into <see cref="s_Rows"/> the first time a note is touched.</summary>
        static void EnsureLoaded()
        {
            if (s_Rows != null)
                return;

            s_Rows = new List<BetaFeedbackEntry>(32);
            string path = FilePath;
            if (!File.Exists(path))
                return;

            // [STANDARD] One read when the first RPC arrives, then the list stays in memory
            // until the process exits. Play Mode domain reload clears the static.
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParse(lines[i], out BetaFeedbackEntry row))
                    continue;
                s_Rows.Add(row);
            }
        }

        /// <summary>Writes the header and every in-memory row. Used after a delete.</summary>
        static void Rewrite()
        {
            var sb = new StringBuilder(s_Rows.Count * 96 + Header.Length);
            sb.Append(Header);
            for (int i = 0; i < s_Rows.Count; i++)
                sb.Append(Encode(s_Rows[i]));
            File.WriteAllText(FilePath, sb.ToString(), Utf8);
        }

        /// <summary>Next id is one past the highest id already stored, so a delete does not reuse numbers.</summary>
        static int NextId()
        {
            int max = 0;
            for (int i = 0; i < s_Rows.Count; i++)
            {
                if (s_Rows[i].Id > max)
                    max = s_Rows[i].Id;
            }

            return max + 1;
        }

        /// <summary>Logs the path once per process so the Console shows where to read the notes.</summary>
        static void AnnounceOnce(string path)
        {
            if (s_AnnouncedPath)
                return;
            s_AnnouncedPath = true;
            Debug.Log("[BetaFeedback] Notes file: " + path);
        }

        /// <summary>One log line. Tabs and newlines inside a field become spaces so the row stays one line.</summary>
        static string Encode(BetaFeedbackEntry row)
        {
            return row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + row.Player
                + "\t" + row.Ship
                + "\t" + row.Gun
                + "\t" + row.ShipLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + row.Branch.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + row.Abilities
                + "\t" + row.Note
                + "\n";
        }

        /// <summary>
        /// Parses one data line. Header lines and broken lines return false and are left out of memory.
        /// A later delete rewrites the file from memory, so a broken line is dropped on the next delete.
        /// </summary>
        static bool TryParse(string line, out BetaFeedbackEntry row)
        {
            row = default;
            if (string.IsNullOrEmpty(line) || line[0] == '#')
                return false;

            // [STANDARD] Eight columns. The note is the last column and may be empty.
            string[] parts = line.Split('\t');
            if (parts.Length < 8)
                return false;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int id) || id <= 0)
                return false;
            if (!int.TryParse(parts[4], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int level))
                return false;
            if (!int.TryParse(parts[5], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int branch))
                return false;

            row = new BetaFeedbackEntry
            {
                Id = id,
                Player = parts[1] ?? string.Empty,
                Ship = parts[2] ?? string.Empty,
                Gun = parts[3] ?? string.Empty,
                ShipLevel = level,
                Branch = branch,
                Abilities = parts[6] ?? string.Empty,
                Note = parts[7] ?? string.Empty,
            };
            return true;
        }

        /// <summary>
        /// Strips characters that would break a one-line tab file, then caps length.
        /// Empty input becomes an empty string (the caller decides the fallback word).
        /// </summary>
        public static string CleanField(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (maxLength < 1)
                maxLength = 1;

            var sb = new StringBuilder(Math.Min(value.Length, maxLength));
            for (int i = 0; i < value.Length && sb.Length < maxLength; i++)
            {
                char c = value[i];
                if (c == '\t' || c == '\n' || c == '\r' || char.IsControl(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// One saved field note. The hull fields are the stamp taken when the tester opened NOTES.
    /// There is no clock column.
    /// </summary>
    public struct BetaFeedbackEntry
    {
        /// <summary>Positive id. Delete names this.</summary>
        public int Id;

        /// <summary>Player name at send time.</summary>
        public string Player;

        /// <summary>Family and chassis they owned.</summary>
        public string Ship;

        /// <summary>Gun they were firing.</summary>
        public string Gun;

        /// <summary>Ship level. 0 means no hull.</summary>
        public int ShipLevel;

        /// <summary>Branch inside that level.</summary>
        public int Branch;

        /// <summary>Added ability levels, or "none".</summary>
        public string Abilities;

        /// <summary>What they typed. May be empty.</summary>
        public string Note;
    }
}
