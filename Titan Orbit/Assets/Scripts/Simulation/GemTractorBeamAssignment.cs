using System.Collections.Generic;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared wing↔gem matching for tractor beams.
    /// Used by server <c>GemTractorBeamSystem</c> only — that system writes ghosted
    /// <c>GemMotionState</c> locks. Client beam VFX presents those locks; it does not
    /// re-run this matcher (a second assignment was how beams latched onto uncollectable gems).
    /// <para>
    /// Rules ([TITAN-ORBIT]):
    /// 1. <b>Sticky lock</b> — once a wing locks a gem, it keeps that gem until the gem leaves
    ///    that wing's search radius (ship rotation must not hand the gem to another wing).
    ///    When PrimaryStickyOnly is true (designer default on TractorBeamSettings), only the
    ///    <b>primary</b> beam on each gem is persisted sticky; assist beams re-evaluate every
    ///    tick so they can jump to newly appeared gems.
    /// 2. <b>Primary fill</b> — free wings claim unclaimed gems first (shortest pairs), so a gem
    ///    field keeps as many wings busy as there are distinct gems in range.
    /// 3. <b>Assist</b> — leftover free wings may join an already-locked gem and stack pull
    ///    (primary 100% + each assist via <see cref="GemTractorBeamMath.StackedBeamPullScale"/>),
    ///    capped by MaxCooperatingBeams (1 = never stack). Never steals a sticky lock.
    /// </para>
    /// Tunables come from <c>TractorBeamSettings</c> at the call site — this class stays free of
    /// ScriptableObject / UnityEngine so Simulation stays Burst-friendly and assembly-light.
    /// </summary>
    public static class GemTractorBeamAssignment
    {
        /// <summary>
        /// One wing–gem sample still inside that wing's search radius this tick.
        /// </summary>
        public struct Candidate
        {
            /// <summary>Index into the ship's wing tractor buffer.</summary>
            public int WingIndex;

            /// <summary>
            /// Server-side gem key for this tick: packed <c>Entity.Version</c> (high 32) +
            /// <c>Entity.Index</c> (low 32). Index alone reused after DestroyEntity and could
            /// sticky-lock a brand-new gem that was never the crystal the wing was pulling.
            /// </summary>
            public long GemKey;

            /// <summary>Toroidal XZ distance from wing origin to gem (world units).</summary>
            public float Dist;
        }

        /// <summary>
        /// One accepted lock this tick: this wing pulls this gem.
        /// The same <see cref="Pair.GemKey"/> may appear on multiple pairs when spare wings assist.
        /// </summary>
        public struct Pair
        {
            public int WingIndex;

            /// <summary>Packed entity key — see <see cref="Candidate.GemKey"/>.</summary>
            public long GemKey;

            /// <summary>
            /// True when this wing is the gem's primary owner (ghost <c>TractorWingIndex</c>).
            /// Assists are false — they stack pull but do not replace the sticky primary.
            /// </summary>
            public bool IsPrimary;
        }

        /// <summary>
        /// Assigns wings for one ship this tick. Writes <paramref name="results"/> and updates
        /// <paramref name="wingToGemLocks"/> (sticky state carried to the next tick).
        /// </summary>
        /// <param name="candidates">All in-range wing↔gem samples for this ship.</param>
        /// <param name="wingCount">Ship wing buffer length.</param>
        /// <param name="wingToGemLocks">
        /// Sticky map wingIndex → packed gem key. Honored when still in range; rewritten for next tick.
        /// When <paramref name="primaryStickyOnly"/> is true, only primary pairs are stored here.
        /// </param>
        /// <param name="results">Output pairs (cleared first). May include assists.</param>
        /// <param name="filteredScratch">Reusable candidate scratch list.</param>
        /// <param name="gemBeamCountScratch">Reusable gem key → how many wings already pull it.</param>
        /// <param name="primaryStickyOnly">
        /// True: only primary locks persist sticky across ticks (assists free to retarget).
        /// False: every active pair is sticky (legacy cling behavior).
        /// </param>
        /// <param name="maxCooperatingBeams">
        /// Max beams on one gem (minimum 1). 1 disables assists entirely.
        /// </param>
        public static void AssignWings(
            List<Candidate> candidates,
            int wingCount,
            Dictionary<int, long> wingToGemLocks,
            List<Pair> results,
            List<Candidate> filteredScratch,
            Dictionary<long, int> gemBeamCountScratch,
            bool primaryStickyOnly,
            int maxCooperatingBeams)
        {
            results.Clear();
            if (wingCount <= 0)
            {
                wingToGemLocks.Clear();
                return;
            }

            if (candidates == null || candidates.Count == 0)
            {
                wingToGemLocks.Clear();
                return;
            }

            // --- Designer cap (1 = never stack) ---
            // [STANDARD] Clamp here so callers can pass raw Inspector values safely.
            int maxBeams = maxCooperatingBeams < 1 ? 1 : maxCooperatingBeams;

            // --- Lookup: is (wing, gem) still in range this tick? ---
            var inRange = new HashSet<(int wing, long gem)>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount)
                    continue;
                inRange.Add((c.WingIndex, c.GemKey));
            }

            var wingClaimed = new bool[wingCount];
            gemBeamCountScratch.Clear();

            // --- Phase 1: honor sticky locks (do not unlock on ship rotate) ---
            // [TITAN-ORBIT] Sticky holds until the gem leaves THIS wing's radius.
            // Cap by maxBeams so mid-session setting changes (e.g. drop to 1) drop extras.
            if (wingToGemLocks.Count > 0)
            {
                var stickyWings = new List<int>(wingToGemLocks.Count);
                foreach (var kv in wingToGemLocks)
                    stickyWings.Add(kv.Key);

                for (int i = 0; i < stickyWings.Count; i++)
                {
                    int wing = stickyWings[i];
                    if (wing < 0 || wing >= wingCount)
                        continue;
                    if (!wingToGemLocks.TryGetValue(wing, out long gemKey))
                        continue;
                    if (!inRange.Contains((wing, gemKey)))
                        continue;

                    // Already at the cooperating cap for this gem → drop this sticky (free the wing).
                    if (gemBeamCountScratch.TryGetValue(gemKey, out int existing) && existing >= maxBeams)
                        continue;

                    wingClaimed[wing] = true;
                    AddBeamCount(gemBeamCountScratch, gemKey);
                    // First sticky beam on a gem is its primary for ghost TractorWingIndex.
                    bool isPrimary = gemBeamCountScratch[gemKey] == 1;
                    results.Add(new Pair { WingIndex = wing, GemKey = gemKey, IsPrimary = isPrimary });
                }
            }

            // --- Phase 2: primary fill — free wings take gems that have no beam yet ---
            // Lets every wing work in a gem field instead of nearest-wing exclusivity starving sides.
            filteredScratch.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount || wingClaimed[c.WingIndex])
                    continue;
                if (gemBeamCountScratch.TryGetValue(c.GemKey, out int count) && count > 0)
                    continue;
                filteredScratch.Add(c);
            }

            filteredScratch.Sort(CompareByDistanceThenWingThenGem);
            for (int i = 0; i < filteredScratch.Count; i++)
            {
                Candidate c = filteredScratch[i];
                if (wingClaimed[c.WingIndex])
                    continue;
                if (gemBeamCountScratch.TryGetValue(c.GemKey, out int count) && count > 0)
                    continue;

                wingClaimed[c.WingIndex] = true;
                AddBeamCount(gemBeamCountScratch, c.GemKey);
                results.Add(new Pair { WingIndex = c.WingIndex, GemKey = c.GemKey, IsPrimary = true });
            }

            // --- Phase 3: assist — spare wings only, stack on gems already locked ---
            // [TITAN-ORBIT] Stacking only when a wing cannot claim a unique free gem, and only
            // up to maxBeams per gem. maxBeams == 1 means this phase never adds anything.
            if (maxBeams > 1)
            {
                filteredScratch.Clear();
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate c = candidates[i];
                    if (c.WingIndex < 0 || c.WingIndex >= wingCount || wingClaimed[c.WingIndex])
                        continue;
                    if (!gemBeamCountScratch.TryGetValue(c.GemKey, out int count) || count <= 0)
                        continue;
                    // Already full — skip candidate so sort/assign do not waste the wing on a full gem.
                    if (count >= maxBeams)
                        continue;
                    filteredScratch.Add(c);
                }

                filteredScratch.Sort(CompareByDistanceThenWingThenGem);
                for (int i = 0; i < filteredScratch.Count; i++)
                {
                    Candidate c = filteredScratch[i];
                    if (wingClaimed[c.WingIndex])
                        continue;
                    if (gemBeamCountScratch.TryGetValue(c.GemKey, out int count) && count >= maxBeams)
                        continue;

                    wingClaimed[c.WingIndex] = true;
                    AddBeamCount(gemBeamCountScratch, c.GemKey);
                    results.Add(new Pair { WingIndex = c.WingIndex, GemKey = c.GemKey, IsPrimary = false });
                }
            }

            // --- Persist sticky map for next tick ---
            // [TITAN-ORBIT] PrimaryStickyOnly: store only IsPrimary pairs so assists re-match
            // every tick and can jump to new gems. Legacy OFF: store every active pair.
            wingToGemLocks.Clear();
            for (int i = 0; i < results.Count; i++)
            {
                var pair = results[i];
                if (primaryStickyOnly && !pair.IsPrimary)
                    continue;
                wingToGemLocks[pair.WingIndex] = pair.GemKey;
            }
        }

        /// <summary>Increments how many beams already claim <paramref name="gemKey"/>.</summary>
        static void AddBeamCount(Dictionary<long, int> counts, long gemKey)
        {
            counts.TryGetValue(gemKey, out int n);
            counts[gemKey] = n + 1;
        }

        /// <summary>
        /// Shortest distance first, then stable wing/gem keys for deterministic ties.
        /// </summary>
        static int CompareByDistanceThenWingThenGem(Candidate a, Candidate b)
        {
            int byDist = a.Dist.CompareTo(b.Dist);
            if (byDist != 0)
                return byDist;

            int byWing = a.WingIndex.CompareTo(b.WingIndex);
            if (byWing != 0)
                return byWing;

            return a.GemKey.CompareTo(b.GemKey);
        }
    }
}
