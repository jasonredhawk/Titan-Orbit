using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One word in the hold-S comms matrix. The network never sends this string — only the
    /// byte index into <see cref="ShipCommsKeywordCatalog"/>. Append new rows at the end so
    /// old indices stay valid after a rebuild.
    /// </summary>
    [Serializable]
    public struct ShipCommsKeyword
    {
        /// <summary>Player-facing chip text (for example "No Problem" or "GG").</summary>
        public string label;

        /// <summary>Which banner the compose panel groups this tile under.</summary>
        public ShipCommsKeywordCategory category;
    }

    /// <summary>
    /// Compose-panel section. Tactical / Subject / Social is a UI grouping only — the RPC
    /// still carries a flat byte index.
    /// </summary>
    public enum ShipCommsKeywordCategory : byte
    {
        /// <summary>Orders and combat verbs (Attack, Defend, Wait…).</summary>
        Tactical = 0,

        /// <summary>Who or what the callout is about (You, Me, Base…).</summary>
        Subject = 1,

        /// <summary>Sportsmanship and yes/no (GG, Thanks, No Problem…).</summary>
        Social = 2,

        /// <summary>
        /// Legacy map-noun bucket. New rows live under <see cref="Subject"/>; the compose
        /// panel folds this category into SUBJECT so we do not show a fourth section.
        /// </summary>
        Objects = 3,

        /// <summary>
        /// Faction color names (Red / Blue / Green / Orange / Purple). The compose panel
        /// paints these as their own TEAM section at the top.
        /// </summary>
        TeamColor = 4,

        /// <summary>
        /// Command-deck words (Everyone, Escort, Form Up, …). The compose panel shows these
        /// under COMMANDER; they only send when the speaker is a top-three commander
        /// on the Commander channel. Wire indices still work for older clients.
        /// </summary>
        Commander = 5,
    }

    /// <summary>
    /// Shared keyword list for the hold-S comms feature. Client UI paints tiles from this
    /// asset; the dedicated server validates RPC indices against the same list.
    /// <para>
    /// ScriptableObject — a Unity data asset designers can edit without changing code.
    /// Lives at <c>Assets/Resources/ShipCommsKeywordCatalog.asset</c> so both the player
    /// build and the Linux headless server can <c>Resources.Load</c> it. If the asset is
    /// missing, <see cref="GetEffectiveKeywords"/> falls back to the hard-coded starter set
    /// so a broken Resources folder cannot silence comms on GCE.
    /// </para>
    /// Indices are append-only: never reorder or delete a shipped keyword. Clients and the
    /// headless binary must agree on what byte <c>0</c> means.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipCommsKeywordCatalog",
        menuName = "Titan Orbit/Ship Comms Keyword Catalog",
        order = 70)]
    public class ShipCommsKeywordCatalog : ScriptableObject
    {
        /// <summary>
        /// [UNITY] Resources name for <c>Assets/Resources/ShipCommsKeywordCatalog.asset</c>.
        /// </summary>
        public const string DefaultResourcesName = "ShipCommsKeywordCatalog";

        /// <summary>
        /// Free sentence length (no ad). Slots 4 and 5 unlock together for the rest of
        /// the match after one rewarded ad.
        /// </summary>
        public const int DefaultSequenceLength = 3;

        /// <summary>
        /// [TITAN-ORBIT] Longest sentence the compose panel and RPCs accept after both
        /// extra slots are unlocked.
        /// </summary>
        public const int MaxSequenceLength = 5;

        /// <summary>Designer-editable list. Empty / null at runtime uses <see cref="BuiltInKeywords"/>.</summary>
        [Tooltip("Append-only keyword rows. Do not reorder shipped entries — those bytes are on the wire.")]
        public List<ShipCommsKeyword> keywords = new List<ShipCommsKeyword>();

        /// <summary>
        /// Process-wide loaded asset (or a runtime instance filled from <see cref="BuiltInKeywords"/>).
        /// </summary>
        static ShipCommsKeywordCatalog s_Cached;

        /// <summary>
        /// Append-only starter set. Indices 0–13 shipped first; 14–41 later verbs;
        /// 42–46 are the five team color names; 47+ pad even 5-wide rows. Never reorder
        /// these rows — those bytes are on the wire. "Mine" (index 7) stays for old
        /// clients but the compose panel hides it; use "Mining" under Tactical.
        /// Indices 47–48 were Scout/Rally and now read Transport/Deposit. Subject
        /// Transport (34) and Dock (38) stay on the wire but are hidden from the matrix.
        /// Index 19 shipped as "Them" and now reads "Us" (Subject — the speaker
        /// plus friendlies in range of that hull). Index 20 shipped as "Us" and now reads "Escort"
        /// under <see cref="ShipCommsKeywordCategory.Commander"/> so the matrix
        /// has one Us. "Everyone" (57) stays Commander. Indices 58–65 are commander
        /// verbs. 66–67 (Mines / Rocket) fill the SUBJECT 5-wide grid.
        /// </summary>
        public static readonly ShipCommsKeyword[] BuiltInKeywords =
        {
            new ShipCommsKeyword { label = "Attack", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Defend", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Wait", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Help", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Follow", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "You", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Me", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Mine", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Base", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Yes", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "No", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "No Problem", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Thanks", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "GG", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Retreat", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Hold", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Push", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Incoming", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Heal", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Us", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Escort", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Here", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Enemy", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Ally", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Sorry", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Ready", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Go", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Good", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Planet", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Moon", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Gems", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Troops", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Titan", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Capture", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Transport", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Kill", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Ship", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Team", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Dock", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Bad", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Oops", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Good Luck", category = ShipCommsKeywordCategory.Social },
            // --- Team colors (append-only; indices 42–46) ---
            // [TITAN-ORBIT] Spoken faction names so "Attack Purple Base" names a team.
            // Spellings must match TeamIdExtensions.ToColorName (Red / Blue / Green / Orange / Purple).
            new ShipCommsKeyword { label = "Red", category = ShipCommsKeywordCategory.TeamColor },
            new ShipCommsKeyword { label = "Blue", category = ShipCommsKeywordCategory.TeamColor },
            new ShipCommsKeyword { label = "Green", category = ShipCommsKeywordCategory.TeamColor },
            new ShipCommsKeyword { label = "Orange", category = ShipCommsKeywordCategory.TeamColor },
            new ShipCommsKeyword { label = "Purple", category = ShipCommsKeywordCategory.TeamColor },
            // --- 5-wide row padding (append-only; indices 47+) ---
            new ShipCommsKeyword { label = "Mining", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Transport", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Deposit", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Asteroid", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Home", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Pad", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Turret", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Nice", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Wow", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Later", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Everyone", category = ShipCommsKeywordCategory.Commander },
            // --- Commander verbs (append-only; indices 58+) ---
            // [TITAN-ORBIT] Orders a top-three commander speaks to the squad. Same 5-wide
            // row math as SUBJECT. Do not reorder — those bytes are on the wire.
            new ShipCommsKeyword { label = "Form Up", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Spread", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Focus", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Report", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Status", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Orders", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Advance", category = ShipCommsKeywordCategory.Commander },
            new ShipCommsKeyword { label = "Cover", category = ShipCommsKeywordCategory.Commander },
            // --- Subject row fill (append-only; indices 66–67) ---
            // [TITAN-ORBIT] SUBJECT is 5-wide. Everyone / Escort live on Commander and
            // Mine / Transport / Dock stay hidden, which left the last row two short.
            new ShipCommsKeyword { label = "Mines", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Rocket", category = ShipCommsKeywordCategory.Subject },
        };

        /// <summary>
        /// [UNITY] Domain Reload off leaves statics sticky. Drop the cache so the next Play
        /// reloads the asset instead of a destroyed ScriptableObject.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => s_Cached = null;

        /// <summary>
        /// Loads the Resources asset once, or builds an in-memory fallback from
        /// <see cref="BuiltInKeywords"/>. Safe on dedicated server.
        /// </summary>
        public static ShipCommsKeywordCatalog LoadDefault()
        {
            if (s_Cached != null)
                return s_Cached;

            // --- Resources first ---
            // [UNITY] Resources.Load works in player and UNITY_SERVER builds when the asset
            // sits under a Resources folder.
            s_Cached = Resources.Load<ShipCommsKeywordCatalog>(DefaultResourcesName);
            // Stale 14-word assets from the first ship must not hide append-only rows.
            if (s_Cached != null && s_Cached.HasUsableKeywords()
                && s_Cached.keywords.Count >= BuiltInKeywords.Length)
            {
                MigrateShippedLabels(s_Cached);
                return s_Cached;
            }

            // --- Hard-coded fallback ---
            // [TITAN-ORBIT] Headless GCE must still validate indices if the asset was stripped.
            s_Cached = CreateInstance<ShipCommsKeywordCatalog>();
            s_Cached.keywords = new List<ShipCommsKeyword>(BuiltInKeywords);
            return s_Cached;
        }

        /// <summary>
        /// Rewrites shipped chip labels that kept their wire index. Index 19 was
        /// "Them" and is now "Us"; index 20 was "Us" / "Wing" and is now "Escort". In-memory
        /// only so a stale Resources asset cannot keep the old words after this code
        /// ships. Bytes on the RPC do not change.
        /// </summary>
        static void MigrateShippedLabels(ShipCommsKeywordCatalog catalog)
        {
            if (catalog?.keywords == null || catalog.keywords.Count < 21)
                return;

            // --- Subject Us (was Them) ---
            ShipCommsKeyword who = catalog.keywords[19];
            if (string.Equals(who.label, "Them", StringComparison.OrdinalIgnoreCase))
            {
                who.label = "Us";
                who.category = ShipCommsKeywordCategory.Subject;
                catalog.keywords[19] = who;
            }

            // --- Commander Escort (was Us, then Wing) ---
            ShipCommsKeyword escort = catalog.keywords[20];
            if (string.Equals(escort.label, "Us", StringComparison.OrdinalIgnoreCase)
                || string.Equals(escort.label, "Wing", StringComparison.OrdinalIgnoreCase))
            {
                escort.label = "Escort";
                escort.category = ShipCommsKeywordCategory.Commander;
                catalog.keywords[20] = escort;
            }
        }

        /// <summary>
        /// Keywords the UI and server should use this session. Never returns null or empty.
        /// </summary>
        public IReadOnlyList<ShipCommsKeyword> GetEffectiveKeywords()
        {
            if (HasUsableKeywords())
                return keywords;

            return BuiltInKeywords;
        }

        /// <summary>How many valid indices exist (0 .. Count-1).</summary>
        public int Count => GetEffectiveKeywords().Count;

        /// <summary>
        /// Finds the wire index for a chip label. Used to insert "Here" when the
        /// player pings the docked minimap.
        /// </summary>
        public bool TryGetIndex(string label, out byte index)
        {
            index = 0;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            IReadOnlyList<ShipCommsKeyword> list = GetEffectiveKeywords();
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].label, label, StringComparison.OrdinalIgnoreCase))
                    continue;
                index = (byte)i;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="index"/> addresses a keyword in the effective list.
        /// </summary>
        /// <param name="index">Byte from the RPC payload.</param>
        public bool IsValidIndex(byte index)
        {
            return index < Count;
        }

        /// <summary>
        /// Resolves a wire index to the chip label. Returns false for out-of-range bytes
        /// so a stale client cannot paint garbage.
        /// </summary>
        /// <param name="index">Keyword byte from <c>ShipCommsCommand</c>.</param>
        /// <param name="label">Player-facing text when true.</param>
        public bool TryGetLabel(byte index, out string label)
        {
            IReadOnlyList<ShipCommsKeyword> list = GetEffectiveKeywords();
            if (index >= list.Count)
            {
                label = string.Empty;
                return false;
            }

            label = list[index].label;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = string.Empty;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves a color-keyword index to the faction tint (Red → TeamA red, …).
        /// False for ordinary words so the compose panel keeps the cyan chrome.
        /// </summary>
        /// <param name="index">Keyword byte from the RPC or a clicked tile.</param>
        /// <param name="color">Canonical team RGB when true.</param>
        public bool TryGetTeamColor(byte index, out UnityEngine.Color color)
        {
            color = default;
            if (!TryGetLabel(index, out string label))
                return false;

            // [TITAN-ORBIT] Color chips share spellings with TeamIdExtensions.ToColorName.
            if (!TeamIdExtensions.TryParseColorName(label, out TeamId team))
                return false;

            color = team.ToColor();
            return true;
        }

        /// <summary>
        /// True when this row belongs on the COMMANDER banner. Category is the
        /// source of truth; the label fallback keeps "Escort" / "Everyone" on the
        /// command deck if an older Resources asset still tags them as Subject.
        /// "Us" is a Subject word (speaker plus friendlies near that hull) — not commander.
        /// </summary>
        /// <param name="word">Catalog row (wire index unchanged).</param>
        public static bool IsCommanderWord(in ShipCommsKeyword word)
        {
            if (word.category == ShipCommsKeywordCategory.Commander)
                return true;
            return IsCommanderLabel(word.label);
        }

        /// <summary>
        /// True when <paramref name="index"/> is a commander-only keyword in the
        /// effective list. The server uses this to reject spoofed command sentences.
        /// </summary>
        /// <param name="index">Keyword byte from the RPC payload.</param>
        public bool IsCommanderKeyword(byte index)
        {
            IReadOnlyList<ShipCommsKeyword> list = GetEffectiveKeywords();
            if (index >= list.Count)
                return false;
            return IsCommanderWord(list[index]);
        }

        /// <summary>
        /// True when any live slot in a 5-word sentence is a command-deck word
        /// (Everyone, Escort, Form Up, …). Used by presentation to keep commander
        /// path lines after the chip row fades, and by the server to reject spoofs.
        /// </summary>
        /// <param name="count">How many keywords were typed (1–5).</param>
        /// <param name="k0">First wire index.</param>
        /// <param name="k1">Second wire index.</param>
        /// <param name="k2">Third wire index.</param>
        /// <param name="k3">Fourth wire index.</param>
        /// <param name="k4">Fifth wire index.</param>
        public bool SequenceUsesCommanderKeyword(byte count, byte k0, byte k1, byte k2, byte k3, byte k4)
        {
            if (count >= 1 && IsCommanderKeyword(k0))
                return true;
            if (count >= 2 && IsCommanderKeyword(k1))
                return true;
            if (count >= 3 && IsCommanderKeyword(k2))
                return true;
            if (count >= 4 && IsCommanderKeyword(k3))
                return true;
            return count >= 5 && IsCommanderKeyword(k4);
        }

        /// <summary>
        /// True when this chip label is a command-deck word (Everyone, Escort, Form Up, …).
        /// Used by the compose panel and chip paint when the catalog row is not handy.
        /// </summary>
        /// <param name="label">Player-facing chip text.</param>
        public static bool IsCommanderLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            return string.Equals(label, "Everyone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Escort", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Wing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Form Up", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Spread", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Focus", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Report", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Status", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Orders", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Advance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "Cover", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the compose matrix should hide this row. Wire indices stay.
        /// "Mine" shipped as a subject; mining is Tactical "Mining".
        /// Subject "Transport" / "Dock" shipped first; those verbs now live under Tactical
        /// as Transport / Deposit.
        /// </summary>
        public static bool IsHiddenFromMatrix(in ShipCommsKeyword word)
        {
            if (string.IsNullOrWhiteSpace(word.label))
                return true;
            if (string.Equals(word.label, "Mine", StringComparison.OrdinalIgnoreCase))
                return true;
            if (word.category != ShipCommsKeywordCategory.Subject)
                return false;
            return string.Equals(word.label, "Transport", StringComparison.OrdinalIgnoreCase)
                || string.Equals(word.label, "Dock", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Server-side sequence check: count is 1–5 and every used index is in range.
        /// Unused slots after <paramref name="count"/> are ignored (may be 0).
        /// </summary>
        public bool IsValidSequence(
            byte count, byte k0, byte k1, byte k2, byte k3, byte k4)
        {
            if (count < 1 || count > MaxSequenceLength)
                return false;

            if (!IsValidIndex(k0))
                return false;
            if (count >= 2 && !IsValidIndex(k1))
                return false;
            if (count >= 3 && !IsValidIndex(k2))
                return false;
            if (count >= 4 && !IsValidIndex(k3))
                return false;
            if (count >= 5 && !IsValidIndex(k4))
                return false;

            return true;
        }

        /// <summary>
        /// Builds "HELP · DEFEND · BASE" for a recent-row chip or a debug label.
        /// </summary>
        public string FormatSentence(
            byte count, byte k0, byte k1, byte k2, byte k3 = 0, byte k4 = 0)
        {
            if (count < 1)
                return string.Empty;

            var sb = new System.Text.StringBuilder(48);
            AppendWord(sb, k0);
            if (count >= 2)
            {
                sb.Append(" · ");
                AppendWord(sb, k1);
            }

            if (count >= 3)
            {
                sb.Append(" · ");
                AppendWord(sb, k2);
            }

            if (count >= 4)
            {
                sb.Append(" · ");
                AppendWord(sb, k3);
            }

            if (count >= 5)
            {
                sb.Append(" · ");
                AppendWord(sb, k4);
            }

            return sb.ToString();
        }

        void AppendWord(System.Text.StringBuilder sb, byte index)
        {
            TryGetLabel(index, out string word);
            sb.Append(word.ToUpperInvariant());
        }

        /// <summary>True when the Inspector list has at least one labeled row.</summary>
        bool HasUsableKeywords()
        {
            if (keywords == null || keywords.Count <= 0)
                return false;

            for (int i = 0; i < keywords.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(keywords[i].label))
                    return true;
            }

            return false;
        }
    }
}
