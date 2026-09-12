using System;
using System.Collections.Generic;
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
        /// [TITAN-ORBIT] Longest sentence the compose panel and RPCs accept.
        /// Three chips stay readable above a moving hull.
        /// </summary>
        public const int MaxSequenceLength = 3;

        /// <summary>Designer-editable list. Empty / null at runtime uses <see cref="BuiltInKeywords"/>.</summary>
        [Tooltip("Append-only keyword rows. Do not reorder shipped entries — those bytes are on the wire.")]
        public List<ShipCommsKeyword> keywords = new List<ShipCommsKeyword>();

        /// <summary>
        /// Process-wide loaded asset (or a runtime instance filled from <see cref="BuiltInKeywords"/>).
        /// </summary>
        static ShipCommsKeywordCatalog s_Cached;

        /// <summary>
        /// Append-only starter set. Indices 0–13 shipped first; 14+ were added later.
        /// Never reorder these rows — those bytes are on the wire.
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
            new ShipCommsKeyword { label = "Them", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Us", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Here", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Enemy", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Ally", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Sorry", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Ready", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Go", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Nice", category = ShipCommsKeywordCategory.Social },
            new ShipCommsKeyword { label = "Planet", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Moon", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Gems", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Troops", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Titan", category = ShipCommsKeywordCategory.Subject },
            new ShipCommsKeyword { label = "Capture", category = ShipCommsKeywordCategory.Tactical },
            new ShipCommsKeyword { label = "Transport", category = ShipCommsKeywordCategory.Subject },
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
                return s_Cached;

            // --- Hard-coded fallback ---
            // [TITAN-ORBIT] Headless GCE must still validate indices if the asset was stripped.
            s_Cached = CreateInstance<ShipCommsKeywordCatalog>();
            s_Cached.keywords = new List<ShipCommsKeyword>(BuiltInKeywords);
            return s_Cached;
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
        /// Server-side sequence check: count is 1–3 and every used index is in range.
        /// Unused slots after <paramref name="count"/> are ignored (may be 0).
        /// </summary>
        public bool IsValidSequence(byte count, byte k0, byte k1, byte k2)
        {
            if (count < 1 || count > MaxSequenceLength)
                return false;

            if (!IsValidIndex(k0))
                return false;
            if (count >= 2 && !IsValidIndex(k1))
                return false;
            if (count >= 3 && !IsValidIndex(k2))
                return false;

            return true;
        }

        /// <summary>
        /// Builds "HELP · DEFEND · BASE" for a recent-row chip or a debug label.
        /// </summary>
        public string FormatSentence(byte count, byte k0, byte k1, byte k2)
        {
            if (count < 1)
                return string.Empty;

            TryGetLabel(k0, out string a);
            if (count == 1)
                return a.ToUpperInvariant();

            TryGetLabel(k1, out string b);
            if (count == 2)
                return a.ToUpperInvariant() + " · " + b.ToUpperInvariant();

            TryGetLabel(k2, out string c);
            return a.ToUpperInvariant() + " · " + b.ToUpperInvariant() + " · " + c.ToUpperInvariant();
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
