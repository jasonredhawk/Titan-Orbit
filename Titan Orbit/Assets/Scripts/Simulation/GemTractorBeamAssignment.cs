using System.Collections.Generic;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared wing↔gem matching for tractor beams.
    /// Used by server <c>GemTractorBeamSystem</c> (authoritative pull) and client
    /// <c>GemTractorBeamClientLogic</c> (beam VFX) so both sides pick the same pairs.
    /// <para>
    /// Rules ([TITAN-ORBIT]):
    /// 1. <b>Sticky lock</b> — once a wing locks a gem, it keeps that gem until the gem leaves
    ///    that wing's search radius (ship rotation must not hand the gem to another wing).
    /// 2. <b>Primary fill</b> — free wings claim unclaimed gems first (shortest pairs), so a gem
    ///    field keeps as many wings busy as there are distinct gems in range.
    /// 3. <b>Assist</b> — only leftover free wings (no unique gem left) may join an already-locked
    ///    gem and stack pull (primary 100% + each assist 25% via
    ///    <see cref="GemTractorBeamMath.StackedBeamPullScale"/>). Never steal a sticky lock.
    /// </para>
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

            /// <summary>Stable gem id for this frame (usually <c>Entity.Index</c>).</summary>
            public int GemId;

            /// <summary>Toroidal XZ distance from wing origin to gem (world units).</summary>
            public float Dist;
        }

        /// <summary>
        /// One accepted lock this tick: this wing pulls this gem.
        /// The same <see cref="GemId"/> may appear on multiple pairs when spare wings assist.
        /// </summary>
        public struct Pair
        {
            public int WingIndex;
            public int GemId;

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
        /// Sticky map wingIndex → gemId. Honored when still in range; rewritten for next tick.
        /// </param>
        /// <param name="results">Output pairs (cleared first). May include assists.</param>
        /// <param name="filteredScratch">Reusable candidate scratch list.</param>
        /// <param name="gemBeamCountScratch">Reusable gemId → how many wings already pull it.</param>
        public static void AssignWings(
            List<Candidate> candidates,
            int wingCount,
            Dictionary<int, int> wingToGemLocks,
            List<Pair> results,
            List<Candidate> filteredScratch,
            Dictionary<int, int> gemBeamCountScratch)
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

            // --- Lookup: is (wing, gem) still in range this tick? ---
            // Key = wingIndex << 32 | (uint)gemId — avoids a nested dictionary alloc.
            var inRange = new HashSet<long>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount)
                    continue;
                inRange.Add(PackWingGem(c.WingIndex, c.GemId));
            }

            var wingClaimed = new bool[wingCount];
            gemBeamCountScratch.Clear();

            // --- Phase 1: honor sticky locks (do not unlock on ship rotate) ---
            // [TITAN-ORBIT] User rule: lock holds until the gem leaves THIS wing's radius.
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
                    if (!wingToGemLocks.TryGetValue(wing, out int gemId))
                        continue;
                    if (!inRange.Contains(PackWingGem(wing, gemId)))
                        continue;

                    wingClaimed[wing] = true;
                    AddBeamCount(gemBeamCountScratch, gemId);
                    // First sticky beam on a gem is its primary for ghost TractorWingIndex.
                    bool isPrimary = gemBeamCountScratch[gemId] == 1;
                    results.Add(new Pair { WingIndex = wing, GemId = gemId, IsPrimary = isPrimary });
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
                if (gemBeamCountScratch.TryGetValue(c.GemId, out int count) && count > 0)
                    continue;
                filteredScratch.Add(c);
            }

            filteredScratch.Sort(CompareByDistanceThenWingThenGem);
            for (int i = 0; i < filteredScratch.Count; i++)
            {
                Candidate c = filteredScratch[i];
                if (wingClaimed[c.WingIndex])
                    continue;
                if (gemBeamCountScratch.TryGetValue(c.GemId, out int count) && count > 0)
                    continue;

                wingClaimed[c.WingIndex] = true;
                AddBeamCount(gemBeamCountScratch, c.GemId);
                results.Add(new Pair { WingIndex = c.WingIndex, GemId = c.GemId, IsPrimary = true });
            }

            // --- Phase 3: assist — spare wings only, stack on gems already locked ---
            // [TITAN-ORBIT] Stacking is allowed only when a wing cannot claim a unique free gem.
            filteredScratch.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount || wingClaimed[c.WingIndex])
                    continue;
                if (!gemBeamCountScratch.TryGetValue(c.GemId, out int count) || count <= 0)
                    continue;
                filteredScratch.Add(c);
            }

            filteredScratch.Sort(CompareByDistanceThenWingThenGem);
            for (int i = 0; i < filteredScratch.Count; i++)
            {
                Candidate c = filteredScratch[i];
                if (wingClaimed[c.WingIndex])
                    continue;

                wingClaimed[c.WingIndex] = true;
                AddBeamCount(gemBeamCountScratch, c.GemId);
                results.Add(new Pair { WingIndex = c.WingIndex, GemId = c.GemId, IsPrimary = false });
            }

            // --- Persist sticky map for next tick (every active wing lock) ---
            wingToGemLocks.Clear();
            for (int i = 0; i < results.Count; i++)
            {
                var pair = results[i];
                wingToGemLocks[pair.WingIndex] = pair.GemId;
            }
        }

        static void AddBeamCount(Dictionary<int, int> counts, int gemId)
        {
            counts.TryGetValue(gemId, out int n);
            counts[gemId] = n + 1;
        }

        static long PackWingGem(int wingIndex, int gemId) => ((long)wingIndex << 32) | (uint)gemId;

        static int CompareByDistanceThenWingThenGem(Candidate a, Candidate b)
        {
            int byDist = a.Dist.CompareTo(b.Dist);
            if (byDist != 0)
                return byDist;

            int byWing = a.WingIndex.CompareTo(b.WingIndex);
            if (byWing != 0)
                return byWing;

            return a.GemId.CompareTo(b.GemId);
        }
    }
}
