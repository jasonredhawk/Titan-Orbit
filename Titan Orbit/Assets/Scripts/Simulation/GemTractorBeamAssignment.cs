using System.Collections.Generic;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared wing↔gem matching for tractor beams.
    /// Used by server <c>GemTractorBeamSystem</c> (authoritative pull) and client
    /// <c>GemTractorBeamClientLogic</c> (beam VFX + presentation pull) so both sides pick the same pairs.
    /// <para>
    /// Rules ([TITAN-ORBIT]):
    /// 1. Each gem may only be claimed by its <b>nearest</b> wing in range (stops opposite-side crisscross).
    /// 2. Each wing gets at most one gem; each gem at most one wing (no stacked pull from idle beams).
    /// 3. Among exclusive nearest candidates, globally shortest pairs win first.
    /// </para>
    /// </summary>
    public static class GemTractorBeamAssignment
    {
        /// <summary>
        /// One wing–gem pair still inside that wing's search radius, before exclusivity filtering.
        /// </summary>
        public struct Candidate
        {
            /// <summary>Index into the ship's <c>ShipWingTractorBeamElement</c> buffer.</summary>
            public int WingIndex;

            /// <summary>Stable gem id for this frame (usually <c>Entity.Index</c>).</summary>
            public int GemId;

            /// <summary>Toroidal XZ distance from wing origin to gem (world units).</summary>
            public float Dist;
        }

        /// <summary>One accepted assignment: this wing pulls this gem (and only this gem).</summary>
        public struct Pair
        {
            public int WingIndex;
            public int GemId;
        }

        /// <summary>
        /// Distance epsilon so floating-point ties still count as "nearest wing" for a gem.
        /// </summary>
        const float NearestWingEpsilon = 0.001f;

        /// <summary>
        /// Fills <paramref name="results"/> with at most one gem per wing and one wing per gem.
        /// Clears <paramref name="results"/> first. Does not allocate beyond the provided lists.
        /// </summary>
        /// <param name="candidates">
        /// All in-range wing–gem samples for one ship this tick. May contain the same gem under
        /// several wings — exclusivity keeps only the nearest wing for each gem.
        /// </param>
        /// <param name="wingCount">Ship wing buffer length (sizes the wing-claimed mask).</param>
        /// <param name="results">Output pairs (wing → gem).</param>
        /// <param name="nearestDistByGem">
        /// Scratch map gemId → nearest wing distance. Cleared and reused by the caller across ships.
        /// </param>
        /// <param name="filteredScratch">
        /// Scratch list for nearest-wing-only candidates. Cleared and reused by the caller.
        /// </param>
        public static void AssignOneGemPerWing(
            List<Candidate> candidates,
            int wingCount,
            List<Pair> results,
            Dictionary<int, float> nearestDistByGem,
            List<Candidate> filteredScratch)
        {
            results.Clear();
            if (candidates == null || candidates.Count == 0 || wingCount <= 0)
                return;

            // --- Phase 1: per-gem nearest wing distance ---
            // [TITAN-ORBIT] A gem sitting between two wings must not be pullable by the far wing.
            // That far link is what reads as "opposite wing crisscross."
            nearestDistByGem.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount)
                    continue;

                if (!nearestDistByGem.TryGetValue(c.GemId, out float best) || c.Dist < best)
                    nearestDistByGem[c.GemId] = c.Dist;
            }

            // --- Phase 2: keep only nearest-wing candidates ---
            filteredScratch.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.WingIndex < 0 || c.WingIndex >= wingCount)
                    continue;
                if (!nearestDistByGem.TryGetValue(c.GemId, out float best))
                    continue;
                if (c.Dist > best + NearestWingEpsilon)
                    continue;

                filteredScratch.Add(c);
            }

            if (filteredScratch.Count == 0)
                return;

            // --- Phase 3: globally shortest pairs first ---
            // Wing-index-first greedy used to let wing 0 steal a gem that sat closer to wing 1,
            // forcing wing 1 onto a far gem across the hull.
            filteredScratch.Sort(CompareByDistanceThenWingThenGem);

            // --- Phase 4: greedy exclusive assign ---
            // [TITAN-ORBIT] One gem per wing — idle beams do not stack pull on a gem another wing owns.
            var wingClaimed = new bool[wingCount];
            var gemClaimed = new HashSet<int>(filteredScratch.Count);

            for (int i = 0; i < filteredScratch.Count; i++)
            {
                Candidate c = filteredScratch[i];
                if (wingClaimed[c.WingIndex] || gemClaimed.Contains(c.GemId))
                    continue;

                wingClaimed[c.WingIndex] = true;
                gemClaimed.Add(c.GemId);
                results.Add(new Pair { WingIndex = c.WingIndex, GemId = c.GemId });
            }
        }

        /// <summary>
        /// Sort key: shorter beam first, then stable wing/gem indices for determinism.
        /// </summary>
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
