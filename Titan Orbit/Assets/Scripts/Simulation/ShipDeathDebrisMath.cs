using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Deterministic spatial clustering + per-chunk launch for the cosmetic ship-death
    /// breakup. Nearby prefab components become 3–9 rigid chunks; seed + cluster index
    /// → same velocity / spin on every client. No physics.
    /// </summary>
    public static class ShipDeathDebrisMath
    {
        const int MaxSupportedClusters = 12;
        const int KMeansIterations = 5;

        struct SeamPair
        {
            public int PartA;
            public int PartB;
            public float DistSq;
            public bool CrossCluster;
        }

        static readonly List<SeamPair> s_pairScratch = new List<SeamPair>(128);

        /// <summary>
        /// Partitions part XZ offsets into <paramref name="minClusters"/>–
        /// <paramref name="maxClusters"/> spatially local groups. Farthest-point seeds
        /// from a random start, then a few k-means passes. Same seed + offsets → same
        /// labels on every client.
        /// </summary>
        /// <returns>Cluster count (1 when there is only one part).</returns>
        public static int AssignSpatialClusters(
            uint seed,
            List<float3> partOffsets,
            int minClusters,
            int maxClusters,
            List<int> clusterOfPart)
        {
            clusterOfPart.Clear();
            if (partOffsets == null || partOffsets.Count == 0)
                return 0;

            int partCount = partOffsets.Count;
            for (int i = 0; i < partCount; i++)
                clusterOfPart.Add(0);

            if (partCount == 1)
                return 1;

            int maxK = math.clamp(maxClusters, 1, math.min(MaxSupportedClusters, partCount));
            int minK = math.clamp(minClusters, 1, maxK);
            var rng = new Random(MixSeed(seed, 4111));
            int k = rng.NextInt(minK, maxK + 1);

            Span<float3> centroids = stackalloc float3[MaxSupportedClusters];
            Span<int> seedPart = stackalloc int[MaxSupportedClusters];

            // --- Farthest-point seeds from a random first part ---
            // A different start (per death seed) splits the hull along a new seam.
            seedPart[0] = rng.NextInt(0, partCount);
            centroids[0] = FlattenXz(partOffsets[seedPart[0]]);
            for (int c = 1; c < k; c++)
            {
                int best = -1;
                float bestMin = -1f;
                for (int i = 0; i < partCount; i++)
                {
                    if (AlreadySeeded(seedPart, c, i))
                        continue;

                    float3 p = FlattenXz(partOffsets[i]);
                    float nearest = DistSqXz(p, centroids[0]);
                    for (int s = 1; s < c; s++)
                        nearest = math.min(nearest, DistSqXz(p, centroids[s]));

                    if (nearest > bestMin)
                    {
                        bestMin = nearest;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    k = c;
                    break;
                }

                seedPart[c] = best;
                centroids[c] = FlattenXz(partOffsets[best]);
            }

            // --- k-means: pull each centroid to the mean of its nearby parts ---
            Span<float3> newCentroids = stackalloc float3[MaxSupportedClusters];
            Span<int> clusterSize = stackalloc int[MaxSupportedClusters];
            for (int iter = 0; iter < KMeansIterations; iter++)
            {
                AssignNearest(partOffsets, centroids, k, clusterOfPart);
                RecomputeCentroids(partOffsets, clusterOfPart, k, newCentroids, clusterSize);
                RepairEmptyClusters(partOffsets, clusterOfPart, k, newCentroids, clusterSize);
                for (int c = 0; c < k; c++)
                    centroids[c] = newCentroids[c];
            }

            AssignNearest(partOffsets, centroids, k, clusterOfPart);
            return k;
        }

        /// <summary>
        /// Builds crash + breakup velocity and Euler spin (deg/s) for one chunk.
        /// Flight direction + last-hit impulse (and its hull contact) set the wreck heading;
        /// a smaller radial cone is the only peel-apart. Same seed → same launch on every client.
        /// </summary>
        /// <param name="seed">16-bit seed from <c>ShipDeathVfxState</c>.</param>
        /// <param name="clusterIndex">Stable index of this chunk (0..k-1).</param>
        /// <param name="clusterOffsetFromCenter">Chunk COM XZ minus ship center XZ.</param>
        /// <param name="impulseDir">Unit XZ kill direction (bullet velocity / ram normal).</param>
        /// <param name="power01">Packed power 0–1.</param>
        /// <param name="hullRadius">Approx hull radius for blast falloff.</param>
        /// <param name="shipVelocity">Ghosted ship velocity — wrecks keep flying this way.</param>
        /// <param name="settings">Designer knobs.</param>
        public static void ComputeClusterLaunch(
            uint seed,
            int clusterIndex,
            float3 clusterOffsetFromCenter,
            float2 impulseDir,
            float power01,
            float hullRadius,
            float3 shipVelocity,
            ShipDeathDebrisSettings settings,
            out float3 velocity,
            out float3 spinDegPerSec)
        {
            float intensity = SampleDeathIntensity(seed, settings);
            var rng = new Random(MixSeed(seed, clusterIndex + 53));

            float3 offset = FlattenXz(clusterOffsetFromCenter);
            float3 radial = math.lengthsq(offset) > 1e-6f
                ? math.normalize(offset)
                : RandomUnitXz(ref rng);

            float3 travel = FlattenXz(shipVelocity);
            if (math.lengthsq(travel) > 1e-6f)
                travel = math.normalize(travel);

            float3 kill = float3.zero;
            float falloff = 0.35f;
            if (math.lengthsq(impulseDir) > 1e-6f)
            {
                kill = math.normalize(new float3(impulseDir.x, 0f, impulseDir.y));
                float radius = math.max(0.25f, hullRadius);
                // Bullet velocity points along the shot. Contact is the far side of that ray.
                float3 impact = -kill * radius;
                float blastR = radius * math.max(0.25f, settings.BlastRadiusHullMul);
                float dist = math.distance(offset, impact);
                falloff = 1f - math.saturate(dist / math.max(0.05f, blastR));
            }

            // --- Crash heading: keep flying, plus the last hit's push ---
            // Chase-kill (shot from behind) adds to forward. Head-on fights it.
            float3 crash = travel + kill * (settings.KillAimWeight * power01);
            if (math.lengthsq(crash) > 1e-8f)
                crash = math.normalize(crash);
            else
                crash = radial;

            float spread = settings.BreakSpread * rng.NextFloat(0.45f, 1f);
            float chaos = settings.DirectionChaos * rng.NextFloat(0.1f, 0.85f) * (1f - 0.45f * power01);
            float3 dir = crash + radial * spread + RandomUnitXz(ref rng) * chaos;
            if (math.lengthsq(dir) > 1e-8f)
                dir = math.normalize(dir);
            else
                dir = crash;

            float speedMul = rng.NextFloat(
                settings.RadialSpeedRandomMin,
                settings.RadialSpeedRandomMax) * intensity;
            float breakSpeed = settings.RadialSpeed * speedMul * math.lerp(1f, 0.45f, power01);
            float crashBoost = settings.CrashSpeed
                               * intensity
                               * math.lerp(0.35f, 1f, power01)
                               * math.lerp(0.5f, 1f, falloff);
            float3 contactKick = kill * (settings.ImpulseSpeed * power01 * falloff * intensity);

            velocity = FlattenXz(shipVelocity) + crash * crashBoost + dir * breakSpeed + contactKick;
            velocity.y = 0f;

            float spinMul = rng.NextFloat(
                settings.ClusterSpinRandomMin,
                settings.ClusterSpinRandomMax) * intensity;
            float spinBudget = settings.MaxSpinDegreesPerSecond * spinMul * math.lerp(0.7f, 1.15f, power01);
            int dominant = rng.NextInt(0, 3);
            float3 spin = new float3(
                rng.NextFloat(-0.35f, 0.35f),
                rng.NextFloat(-0.35f, 0.35f),
                rng.NextFloat(-0.35f, 0.35f));
            float dominantSign = rng.NextFloat() < 0.5f ? -1f : 1f;
            float dominantMag = rng.NextFloat(0.65f, 1f) * dominantSign;
            if (dominant == 0)
                spin.x = dominantMag;
            else if (dominant == 1)
                spin.y = dominantMag;
            else
                spin.z = dominantMag;

            spinDegPerSec = spin * spinBudget;
        }

        /// <summary>
        /// Closest part pairs that used to touch — cross-cluster tears first, then
        /// in-chunk joints if we still have slots. Each part is used at most once.
        /// </summary>
        public static void CollectSeamContacts(
            List<float3> partOffsets,
            List<int> clusterOfPart,
            int maxPairs,
            List<int> partA,
            List<int> partB)
        {
            partA.Clear();
            partB.Clear();
            s_pairScratch.Clear();
            if (partOffsets == null || clusterOfPart == null || maxPairs <= 0)
                return;

            int partCount = math.min(partOffsets.Count, clusterOfPart.Count);
            if (partCount <= 0)
                return;

            if (partCount == 1)
            {
                partA.Add(0);
                partB.Add(0);
                return;
            }

            for (int i = 0; i < partCount; i++)
            {
                float3 a = FlattenXz(partOffsets[i]);
                int ca = clusterOfPart[i];
                for (int j = i + 1; j < partCount; j++)
                {
                    float3 b = FlattenXz(partOffsets[j]);
                    s_pairScratch.Add(new SeamPair
                    {
                        PartA = i,
                        PartB = j,
                        DistSq = DistSqXz(a, b),
                        CrossCluster = ca != clusterOfPart[j],
                    });
                }
            }

            s_pairScratch.Sort(CompareSeamPair);

            Span<byte> used = stackalloc byte[partCount];
            for (int i = 0; i < s_pairScratch.Count && partA.Count < maxPairs; i++)
            {
                SeamPair pair = s_pairScratch[i];
                if (used[pair.PartA] != 0 || used[pair.PartB] != 0)
                    continue;

                used[pair.PartA] = 1;
                used[pair.PartB] = 1;
                partA.Add(pair.PartA);
                partB.Add(pair.PartB);
            }
        }

        /// <summary>Deterministic first delay, repeat interval, and flash size for one seam emitter.</summary>
        public static void ComputeSeamBurst(
            uint seed,
            int emitterIndex,
            ShipDeathDebrisSettings settings,
            out float firstDelay,
            out float interval,
            out float scale,
            out float3 jitterXz)
        {
            var rng = new Random(MixSeed(seed, emitterIndex + 211));
            float delayMin = math.max(0f, settings.SeamFirstDelayMin);
            float delayMax = math.max(delayMin, settings.SeamFirstDelayMax);
            // Later emitters wait for a second-wave beat instead of all popping at once.
            float wave = math.saturate(emitterIndex * 0.16f + rng.NextFloat(0f, 0.4f));
            firstDelay = math.lerp(delayMin, delayMax, wave);

            float intervalMin = math.max(0.2f, settings.SeamRepeatMin);
            float intervalMax = math.max(intervalMin, settings.SeamRepeatMax);
            interval = rng.NextFloat(intervalMin, intervalMax);

            float scaleMin = math.max(0.02f, settings.SeamScaleMin);
            float scaleMax = math.max(scaleMin, settings.SeamScaleMax);
            scale = rng.NextFloat(scaleMin, scaleMax);

            float jitter = rng.NextFloat(0.02f, 0.08f);
            jitterXz = RandomUnitXz(ref rng) * jitter;
        }

        /// <summary>Applies linear / angular drag for one frame.</summary>
        public static void IntegrateDrag(
            ref float3 velocity,
            ref float3 spinDegPerSec,
            float dt,
            float linearDrag,
            float angularDrag)
        {
            float lin = math.max(0f, 1f - linearDrag * dt);
            float ang = math.max(0f, 1f - angularDrag * dt);
            velocity *= lin;
            spinDegPerSec *= ang;
        }

        /// <summary>
        /// Client-only sphere bounce. <paramref name="bodyToChunkOffset"/> must already be the
        /// toroidal shortest XZ vector (or Euclidean when map size is unset). No physics bodies.
        /// </summary>
        /// <returns>True when the chunk was overlapping and got pushed / reflected.</returns>
        public static bool ResolveVisualSphere(
            ref float3 chunkPos,
            ref float3 velocity,
            ref float3 spinDegPerSec,
            float3 bodyToChunkOffset,
            float chunkRadius,
            float bodyRadius,
            float bounce,
            float hitSpinPerSpeed)
        {
            float minDist = math.max(0.05f, chunkRadius) + math.max(0.05f, bodyRadius);
            float distSq = math.lengthsq(bodyToChunkOffset);
            if (distSq >= minDist * minDist)
                return false;

            float dist = math.sqrt(math.max(1e-8f, distSq));
            float3 n = bodyToChunkOffset / dist;
            chunkPos += n * (minDist - dist);
            chunkPos.y = 0f;

            float vn = math.dot(velocity, n);
            if (vn < 0f)
                velocity -= n * ((1f + math.saturate(bounce)) * vn);
            velocity.y = 0f;

            float3 graze = math.cross(n, velocity);
            spinDegPerSec += graze * math.max(0f, hitSpinPerSpeed);
            return true;
        }

        static float SampleDeathIntensity(uint seed, ShipDeathDebrisSettings settings)
        {
            var rng = new Random(MixSeed(seed, 7919));
            float min = math.max(0.05f, settings.DeathIntensityMin);
            float max = math.max(min, settings.DeathIntensityMax);
            return rng.NextFloat(min, max);
        }

        static void AssignNearest(
            List<float3> partOffsets,
            Span<float3> centroids,
            int k,
            List<int> clusterOfPart)
        {
            int partCount = partOffsets.Count;
            for (int i = 0; i < partCount; i++)
            {
                float3 p = FlattenXz(partOffsets[i]);
                int best = 0;
                float bestD = DistSqXz(p, centroids[0]);
                for (int c = 1; c < k; c++)
                {
                    float d = DistSqXz(p, centroids[c]);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = c;
                    }
                }

                clusterOfPart[i] = best;
            }
        }

        static void RecomputeCentroids(
            List<float3> partOffsets,
            List<int> clusterOfPart,
            int k,
            Span<float3> centroids,
            Span<int> clusterSize)
        {
            for (int c = 0; c < k; c++)
            {
                centroids[c] = float3.zero;
                clusterSize[c] = 0;
            }

            int partCount = partOffsets.Count;
            for (int i = 0; i < partCount; i++)
            {
                int c = clusterOfPart[i];
                centroids[c] += FlattenXz(partOffsets[i]);
                clusterSize[c]++;
            }

            for (int c = 0; c < k; c++)
            {
                if (clusterSize[c] > 0)
                    centroids[c] /= clusterSize[c];
            }
        }

        /// <summary>
        /// Moves the farthest member of the largest cluster into any empty centroid
        /// so k stays honest instead of collapsing to fewer chunks.
        /// </summary>
        static void RepairEmptyClusters(
            List<float3> partOffsets,
            List<int> clusterOfPart,
            int k,
            Span<float3> centroids,
            Span<int> clusterSize)
        {
            for (int empty = 0; empty < k; empty++)
            {
                if (clusterSize[empty] > 0)
                    continue;

                int donor = LargestCluster(clusterSize, k);
                if (donor < 0 || clusterSize[donor] <= 1)
                    continue;

                int steal = FarthestInCluster(partOffsets, clusterOfPart, donor, centroids[donor]);
                if (steal < 0)
                    continue;

                clusterOfPart[steal] = empty;
                clusterSize[donor]--;
                clusterSize[empty] = 1;
                centroids[empty] = FlattenXz(partOffsets[steal]);
                RecenterOne(partOffsets, clusterOfPart, donor, centroids);
            }
        }

        static void RecenterOne(
            List<float3> partOffsets,
            List<int> clusterOfPart,
            int cluster,
            Span<float3> centroids)
        {
            float3 sum = float3.zero;
            int n = 0;
            int partCount = partOffsets.Count;
            for (int i = 0; i < partCount; i++)
            {
                if (clusterOfPart[i] != cluster)
                    continue;
                sum += FlattenXz(partOffsets[i]);
                n++;
            }

            if (n > 0)
                centroids[cluster] = sum / n;
        }

        static int LargestCluster(Span<int> clusterSize, int k)
        {
            int best = -1;
            int bestN = 0;
            for (int c = 0; c < k; c++)
            {
                if (clusterSize[c] > bestN)
                {
                    bestN = clusterSize[c];
                    best = c;
                }
            }

            return best;
        }

        static int FarthestInCluster(
            List<float3> partOffsets,
            List<int> clusterOfPart,
            int cluster,
            float3 centroid)
        {
            int best = -1;
            float bestD = -1f;
            int partCount = partOffsets.Count;
            for (int i = 0; i < partCount; i++)
            {
                if (clusterOfPart[i] != cluster)
                    continue;
                float d = DistSqXz(FlattenXz(partOffsets[i]), centroid);
                if (d > bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        static int CompareSeamPair(SeamPair a, SeamPair b)
        {
            if (a.CrossCluster != b.CrossCluster)
                return a.CrossCluster ? -1 : 1;
            return a.DistSq.CompareTo(b.DistSq);
        }

        static bool AlreadySeeded(Span<int> seedPart, int seededCount, int partIndex)
        {
            for (int i = 0; i < seededCount; i++)
            {
                if (seedPart[i] == partIndex)
                    return true;
            }

            return false;
        }

        static float3 FlattenXz(float3 v)
        {
            v.y = 0f;
            return v;
        }

        static float DistSqXz(float3 a, float3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        static uint MixSeed(uint seed, int partIndex)
        {
            uint s = seed == 0 ? 1u : seed;
            s ^= (uint)(partIndex + 1) * 747796405u;
            if (s == 0)
                s = 1;
            return s;
        }

        static float3 RandomUnitXz(ref Random rng)
        {
            float angle = rng.NextFloat(0f, 2f * math.PI);
            return new float3(math.sin(angle), 0f, math.cos(angle));
        }
    }
}
