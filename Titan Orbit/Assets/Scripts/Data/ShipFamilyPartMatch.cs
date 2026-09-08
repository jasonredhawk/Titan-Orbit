using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Picks host chassis slots for a cross-family part remap.
    /// Catalog ids look like <c>CosmicShark_Engine_2</c>; after
    /// <see cref="ShipFamilyDefinition.NormalizeComponentId"/> that becomes suffix
    /// <c>Engine_2</c> (left/right and mirrored tokens stripped).
    /// <para>
    /// [TITAN-ORBIT] Indices are family-local. Exact suffix wins; otherwise we take the
    /// closest index at or below the purchased number, then the highest index of that type
    /// (Wing_5 on a hull that only has Wing_3 → Wing_3, including any _L/_R siblings).
    /// Engines do not match thrusters. All weapon subtypes share one group for B-key swaps.
    /// </para>
    /// </summary>
    public static class ShipFamilyPartMatch
    {
        static readonly Regex UnderscoreIndexRegex = new Regex(
            @"^(?<body>.+)_(?<num>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly Regex CompactIndexRegex = new Regex(
            @"^(?<body>.*?\D)(?<num>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Hidden child on a live proxy that holds disabled clones of original host parts
        /// so discard / B-key restore can put the authored mesh back.
        /// </summary>
        public const string OriginalStashName = "_OriginalParts";

        /// <summary>One USC-named child on a hull (host or source family prefab).</summary>
        public struct Slot
        {
            /// <summary>Live transform on the hierarchy being scanned.</summary>
            public Transform Transform;
            /// <summary>Canonical Part Profile (Engine, Wing, Weapon Bullet, …).</summary>
            public string PartType;
            /// <summary>Trailing catalog index (Engine_2 → 2). Unnumbered parts use 1.</summary>
            public int Index;
            /// <summary>Suffix after family prefix, e.g. <c>Engine_2</c>.</summary>
            public string NormalizedSuffix;
        }

        /// <summary>
        /// Parses a hierarchy name into part type + index using the host/source family prefix.
        /// </summary>
        public static bool TryParseSlotName(
            string transformName,
            string familyPrefix,
            out string partType,
            out int index,
            out string normalizedSuffix)
        {
            partType = string.Empty;
            index = 1;
            normalizedSuffix = string.Empty;
            if (string.IsNullOrWhiteSpace(transformName))
                return false;

            string normalized = ShipFamilyDefinition.NormalizeComponentId(transformName);
            if (string.IsNullOrEmpty(normalized))
                return false;

            string suffix = normalized;
            if (!string.IsNullOrWhiteSpace(familyPrefix)
                && normalized.StartsWith(familyPrefix.Trim() + "_", StringComparison.OrdinalIgnoreCase))
            {
                suffix = normalized.Substring(familyPrefix.Trim().Length + 1);
            }

            if (string.IsNullOrEmpty(suffix))
                return false;

            normalizedSuffix = suffix;
            partType = ShipFamilyPartTypes.Normalize(
                ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(suffix),
                suffix);
            if (string.IsNullOrEmpty(partType)
                || string.Equals(partType, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase))
                return false;

            TryParseTrailingIndex(suffix, out index);
            return true;
        }

        /// <summary>Walks <paramref name="root"/> and collects every parseable family-prefixed child.</summary>
        public static void CollectSlots(Transform root, string familyPrefix, List<Slot> dest)
        {
            if (dest == null || root == null)
                return;

            dest.Clear();
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == root)
                    continue;
                if (t.name.StartsWith(OriginalStashName, StringComparison.Ordinal))
                    continue;
                if (IsUnderOriginalStash(t))
                    continue;
                if (!TryParseSlotName(t.name, familyPrefix, out string partType, out int index, out string suffix))
                    continue;

                dest.Add(new Slot
                {
                    Transform = t,
                    PartType = partType,
                    Index = index,
                    NormalizedSuffix = suffix,
                });
            }
        }

        /// <summary>
        /// Host slots that should receive <paramref name="sourceComponentId"/> from
        /// <paramref name="sourceFamilyPrefix"/> (exact suffix, else best index).
        /// </summary>
        public static void CollectBestHostSlots(
            IReadOnlyList<Slot> hostSlots,
            string sourceFamilyPrefix,
            string sourceComponentId,
            List<Slot> dest)
        {
            dest?.Clear();
            if (dest == null || hostSlots == null)
                return;
            if (!TryParseSlotName(sourceComponentId, sourceFamilyPrefix, out string partType, out int targetIndex, out _))
                return;

            CollectBestHostSlotsForType(hostSlots, partType, targetIndex, dest);
        }

        /// <summary>
        /// Same best-index pick for a known part type (B-key maps every host weapon
        /// independently to the source family's matching index).
        /// </summary>
        public static void CollectBestHostSlotsForType(
            IReadOnlyList<Slot> hostSlots,
            string partType,
            int targetIndex,
            List<Slot> dest)
        {
            dest?.Clear();
            if (dest == null || hostSlots == null || string.IsNullOrEmpty(partType))
                return;

            int chosen = -1;
            int bestAtOrBelow = -1;
            int bestAny = -1;
            for (int i = 0; i < hostSlots.Count; i++)
            {
                Slot slot = hostSlots[i];
                if (!IsSameScaleGroup(partType, slot.PartType))
                    continue;
                if (slot.Index == targetIndex)
                    chosen = slot.Index;
                if (slot.Index <= targetIndex && slot.Index > bestAtOrBelow)
                    bestAtOrBelow = slot.Index;
                if (slot.Index > bestAny)
                    bestAny = slot.Index;
            }

            if (chosen < 0)
                chosen = bestAtOrBelow >= 0 ? bestAtOrBelow : bestAny;
            if (chosen < 0)
                return;

            for (int i = 0; i < hostSlots.Count; i++)
            {
                Slot slot = hostSlots[i];
                if (!IsSameScaleGroup(partType, slot.PartType))
                    continue;
                if (slot.Index == chosen)
                    dest.Add(slot);
            }
        }

        /// <summary>
        /// Source-family child that matches <paramref name="wantedSuffix"/>, else best index
        /// of the same type on that prefab instance.
        /// </summary>
        public static Transform FindBestSourceTransform(
            Transform sourceRoot,
            string sourceFamilyPrefix,
            string wantedSuffix)
        {
            if (sourceRoot == null || string.IsNullOrWhiteSpace(wantedSuffix))
                return null;

            var slots = new List<Slot>(16);
            CollectSlots(sourceRoot, sourceFamilyPrefix, slots);

            Transform exact = null;
            for (int i = 0; i < slots.Count; i++)
            {
                if (string.Equals(slots[i].NormalizedSuffix, wantedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (slots[i].Transform != null
                        && slots[i].Transform.name.IndexOf("_Mirrored", StringComparison.OrdinalIgnoreCase) < 0)
                        return slots[i].Transform;
                    if (exact == null)
                        exact = slots[i].Transform;
                }
            }

            if (exact != null)
                return exact;

            if (!TryParseSlotName(wantedSuffix, sourceFamilyPrefix, out string partType, out int index, out _))
                return null;

            var best = new List<Slot>(4);
            CollectBestHostSlotsForType(slots, partType, index, best);
            return best.Count > 0 ? best[0].Transform : null;
        }

        /// <summary>Weapons share one group; Engine and Thruster stay separate.</summary>
        public static bool IsSameScaleGroup(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            if (ShipFamilyPartTypes.IsWeapon(a) && ShipFamilyPartTypes.IsWeapon(b))
                return true;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when this part type is a hull weapon (B-key owns the mesh).</summary>
        public static bool IsWeaponPartType(string partType) =>
            ShipFamilyPartTypes.IsWeapon(partType);

        static bool TryParseTrailingIndex(string suffix, out int index)
        {
            index = 1;
            if (string.IsNullOrEmpty(suffix))
                return false;

            Match underscored = UnderscoreIndexRegex.Match(suffix);
            if (underscored.Success
                && int.TryParse(underscored.Groups["num"].Value, out int n)
                && n > 0)
            {
                index = n;
                return true;
            }

            Match compact = CompactIndexRegex.Match(suffix);
            if (compact.Success
                && int.TryParse(compact.Groups["num"].Value, out int c)
                && c > 0)
            {
                index = c;
                return true;
            }

            return false;
        }

        static bool IsUnderOriginalStash(Transform t)
        {
            Transform walk = t;
            while (walk != null)
            {
                if (walk.name == OriginalStashName)
                    return true;
                walk = walk.parent;
            }

            return false;
        }
    }
}
