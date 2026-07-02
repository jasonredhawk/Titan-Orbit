using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>Seed-based procedural layout for ECS map generation (ported from legacy MapGenerator).</summary>
    public static class MapGenerationLogic
    {
        public const int MinSupportedTeams = 2;
        public const int MaxSupportedTeams = 5;
        public const float HomeGemMoonScaleMultiplier = 1.5f;

        const float MinAsteroidRadius = 0.35f;
        const float MaxAsteroidRadius = MinAsteroidRadius * 10f;

        public struct PlanetPlacement
        {
            public float3 Position;
            public float InfluenceRadius;
        }

        public struct RolledParameters
        {
            public uint Seed;
            public float MapWidth;
            public float MapHeight;
            public int TeamCount;
            public int NeutralPlanetCount;
            public int AsteroidCount;
            public int AsteroidClusterCount;
        }

        public struct HomePlanetLayout
        {
            public float3 Position;
            public float Scale;
            public int Level;
        }

        public struct NeutralPlanetLayout
        {
            public float3 Position;
            public float Scale;
            public int Level;
        }

        public struct AsteroidLayout
        {
            public float3 Position;
            public float3 Scale;
            public float GemValue;
        }

        /// <summary>Non-zero seed for one match when MapGenerationSettings.seed is 0 (random each play).</summary>
        public static uint ComputeEphemeralSeed()
        {
            uint tick = unchecked((uint)System.Environment.TickCount);
            uint frame = unchecked((uint)UnityEngine.Time.frameCount);
            uint random = unchecked((uint)UnityEngine.Random.Range(1, int.MaxValue));
            uint seed = math.hash(new uint3(tick, frame, random));
            return seed == 0 ? 1u : seed;
        }

        public static RolledParameters RollParameters(in MapGenerationConfig config, uint fallbackSeed)
        {
            uint seed = config.Seed != 0 ? (uint)config.Seed : fallbackSeed;
            var rng = Random.CreateFromIndex(seed);

            float mapLo = math.min(config.MinMapSize, config.MaxMapSize);
            float mapHi = math.max(config.MinMapSize, config.MaxMapSize);
            float mapSize = rng.NextFloat(mapLo, mapHi);

            int teamLo = math.clamp(math.min(config.MinTeamsPerMatch, config.MaxTeamsPerMatch), MinSupportedTeams, MaxSupportedTeams);
            int teamHi = math.clamp(math.max(config.MinTeamsPerMatch, config.MaxTeamsPerMatch), MinSupportedTeams, MaxSupportedTeams);
            int teamCount = rng.NextInt(teamLo, teamHi + 1);

            int neutralLo = math.min(config.MinNeutralPlanets, config.MaxNeutralPlanets);
            int neutralHi = math.max(config.MinNeutralPlanets, config.MaxNeutralPlanets);
            int neutralCount = rng.NextInt(neutralLo, neutralHi + 1);

            float t = mapHi > mapLo ? math.saturate((mapSize - mapLo) / (mapHi - mapLo)) : 0f;
            int asteroidLo = math.min(config.AsteroidsAtMinMapSize, config.AsteroidsAtMaxMapSize);
            int asteroidHi = math.max(config.AsteroidsAtMinMapSize, config.AsteroidsAtMaxMapSize);
            int asteroidCount = math.max(0, (int)math.round(math.lerp(asteroidLo, asteroidHi, t)));

            int clusterLo = math.min(config.MinAsteroidClusters, config.MaxAsteroidClusters);
            int clusterHi = math.max(config.MinAsteroidClusters, config.MaxAsteroidClusters);
            int clusterCount = asteroidCount > 0 ? math.max(1, rng.NextInt(clusterLo, clusterHi + 1)) : 0;

            return new RolledParameters
            {
                Seed = seed,
                MapWidth = mapSize,
                MapHeight = mapSize,
                TeamCount = teamCount,
                NeutralPlanetCount = neutralCount,
                AsteroidCount = asteroidCount,
                AsteroidClusterCount = clusterCount,
            };
        }

        public static void BuildHomePlanets(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            ref Random rng,
            NativeList<HomePlanetLayout> output,
            NativeList<PlanetPlacement> planetPlacements)
        {
            output.Clear();
            planetPlacements.Clear();

            int teamCount = math.clamp(rolled.TeamCount, MinSupportedTeams, MaxSupportedTeams);
            float homeScale = math.max(0.01f, config.HomePlanetSize);
            int homeLevel = math.max(1, config.HomePlanetLevel);
            float homeInfluence = PlanetGemMoonMath.ComputeMapPlacementInfluenceRadiusWorld(
                homeScale, homeLevel, HomeGemMoonScaleMultiplier);

            var positions = new NativeList<float3>(teamCount, Allocator.Temp);
            BuildRandomHomePositions(config, rolled.MapWidth, rolled.MapHeight, homeInfluence, teamCount, ref rng, positions);

            for (int i = 0; i < positions.Length; i++)
            {
                output.Add(new HomePlanetLayout
                {
                    Position = positions[i],
                    Scale = homeScale,
                    Level = homeLevel,
                });
                planetPlacements.Add(new PlanetPlacement
                {
                    Position = positions[i],
                    InfluenceRadius = homeInfluence,
                });
            }

            positions.Dispose();
        }

        public static void BuildNeutralPlanets(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            ref Random rng,
            NativeList<PlanetPlacement> planetPlacements,
            NativeList<NeutralPlanetLayout> output)
        {
            output.Clear();
            if (rolled.NeutralPlanetCount <= 0)
                return;

            var levels = BuildNeutralStartingLevels(config, rolled.NeutralPlanetCount, ref rng);
            float planetLo = math.min(config.MinPlanetSize, config.MaxPlanetSize);
            float planetHi = math.max(config.MinPlanetSize, config.MaxPlanetSize);

            for (int i = 0; i < rolled.NeutralPlanetCount; i++)
            {
                float size = rng.NextFloat(planetLo, planetHi);
                int level = levels[i];
                float influence = PlanetGemMoonMath.ComputeMapPlacementInfluenceRadiusWorld(size, level);
                float3 position = GetRandomMapPositionAvoidingPlanetRings(
                    config, rolled.MapWidth, rolled.MapHeight, planetPlacements, influence, ref rng);

                output.Add(new NeutralPlanetLayout
                {
                    Position = position,
                    Scale = size,
                    Level = level,
                });
                planetPlacements.Add(new PlanetPlacement
                {
                    Position = position,
                    InfluenceRadius = influence,
                });
            }
        }

        public static void BuildAsteroids(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            ref Random rng,
            NativeList<PlanetPlacement> planetPlacements,
            NativeList<AsteroidLayout> output)
        {
            output.Clear();
            if (rolled.AsteroidCount <= 0 || rolled.AsteroidClusterCount <= 0)
                return;

            var asteroidPositions = new NativeList<float3>(rolled.AsteroidCount, Allocator.Temp);
            int perCluster = (int)math.ceil((float)rolled.AsteroidCount / math.max(1, rolled.AsteroidClusterCount));
            var clusterCenters = PickAsteroidClusterCenters(
                config, rolled, planetPlacements, rolled.AsteroidClusterCount, ref rng);

            float gemLo = math.min(config.MinAsteroidGemValue, config.MaxAsteroidGemValue);
            float gemHi = math.max(config.MinAsteroidGemValue, config.MaxAsteroidGemValue);
            float gemSpan = math.max(0.001f, gemHi - gemLo);

            for (int c = 0; c < rolled.AsteroidClusterCount && output.Length < rolled.AsteroidCount; c++)
            {
                float3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && output.Length < rolled.AsteroidCount; i++)
                {
                    float3 position = GetPositionInCluster(center, perCluster, ref rng);
                    if (IsTooCloseToAny(position, config.MinAsteroidSpacing, asteroidPositions))
                        continue;
                    if (OverlapsPlanetOrbitRings(config, planetPlacements, rolled.MapWidth, rolled.MapHeight, position, MaxAsteroidRadius))
                        continue;

                    asteroidPositions.Add(position);
                    float gemValue = rng.NextFloat(gemLo, gemHi);
                    float linearScale = math.lerp(MinAsteroidRadius, MaxAsteroidRadius, (gemValue - gemLo) / gemSpan);
                    float3 scale = new float3(
                        linearScale * (0.8f + rng.NextFloat() * 0.4f),
                        linearScale * (0.9f + rng.NextFloat() * 0.2f),
                        linearScale * (0.85f + rng.NextFloat() * 0.3f));

                    output.Add(new AsteroidLayout
                    {
                        Position = position,
                        Scale = scale,
                        GemValue = gemValue,
                    });
                }
            }

            clusterCenters.Dispose();
            asteroidPositions.Dispose();
        }

        static int[] BuildNeutralStartingLevels(in MapGenerationConfig config, int count, ref Random rng)
        {
            var levels = new int[count];
            if (count <= 0)
                return levels;

            if (config.RandomizeNeutralStartingLevel == 0)
            {
                for (int i = 0; i < count; i++)
                    levels[i] = 1;
                return levels;
            }

            int minLevel = math.max(1, config.MinNeutralStartingLevel);
            int maxLevel = math.max(minLevel, config.MaxNeutralStartingLevel);
            int span = maxLevel - minLevel + 1;
            int basePerLevel = count / span;
            int remainder = count % span;
            int index = 0;

            for (int level = minLevel; level <= maxLevel; level++)
            {
                int planetsAtLevel = basePerLevel + (level - minLevel < remainder ? 1 : 0);
                for (int i = 0; i < planetsAtLevel && index < count; i++)
                    levels[index++] = level;
            }

            for (int i = count - 1; i > 0; i--)
            {
                int j = rng.NextInt(0, i + 1);
                int tmp = levels[i];
                levels[i] = levels[j];
                levels[j] = tmp;
            }

            return levels;
        }

        static void BuildRandomHomePositions(
            in MapGenerationConfig config,
            float mapWidth,
            float mapHeight,
            float homeInfluence,
            int teamCount,
            ref Random rng,
            NativeList<float3> output)
        {
            output.Clear();
            float ringPairSep = 2f * homeInfluence + math.max(0f, config.PlanetRingPlacementMargin);
            float minSep = math.max(25f, math.max(config.MinHomePlanetPairSeparation, ringPairSep));
            float maxRadius = GetMaxHomePlanetRingRadius(config, mapWidth, mapHeight, homeInfluence);
            float baseRot = rng.NextFloat(0f, math.PI * 2f);

            const int radiusSteps = 24;
            const int rotationSteps = 48;
            NativeList<float3> bestLayout = default;
            float bestScore = float.NegativeInfinity;

            for (int relax = 0; relax < 12; relax++)
            {
                float requiredMin = minSep;
                if (bestLayout.IsCreated)
                    bestLayout.Dispose();
                bestLayout = default;
                bestScore = float.NegativeInfinity;

                for (int ri = 0; ri <= radiusSteps; ri++)
                {
                    float r = maxRadius * (ri + 1f) / (radiusSteps + 1f);
                    for (int ti = 0; ti < rotationSteps; ti++)
                    {
                        float rot = baseRot + (math.PI * 2f * ti) / rotationSteps;
                        var candidate = BuildRegularHomePolygon(teamCount, r, rot);
                        if (!MeetsMinToroidalPairSeparation(candidate, mapWidth, mapHeight, requiredMin))
                        {
                            candidate.Dispose();
                            continue;
                        }

                        float score = ScoreToroidalEquidistance(candidate, mapWidth, mapHeight);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            if (bestLayout.IsCreated)
                                bestLayout.Dispose();
                            bestLayout = candidate;
                        }
                        else
                        {
                            candidate.Dispose();
                        }
                    }
                }

                if (bestLayout.IsCreated)
                {
                    for (int i = 0; i < bestLayout.Length; i++)
                        output.Add(bestLayout[i]);
                    bestLayout.Dispose();
                    return;
                }

                minSep *= 0.92f;
            }

            if (bestLayout.IsCreated)
                bestLayout.Dispose();

            PlaceHomePlanetsFallbackRing(config, mapWidth, mapHeight, homeInfluence, teamCount, ref rng, output);
        }

        static void PlaceHomePlanetsFallbackRing(
            in MapGenerationConfig config,
            float mapWidth,
            float mapHeight,
            float homeInfluence,
            int teamCount,
            ref Random rng,
            NativeList<float3> output)
        {
            output.Clear();
            float ringPairSep = 2f * homeInfluence + math.max(0f, config.PlanetRingPlacementMargin);
            float minSep = math.max(28f, math.max(config.MinHomePlanetPairSeparation * 0.55f, ringPairSep));
            float maxRadius = GetMaxHomePlanetRingRadius(config, mapWidth, mapHeight, homeInfluence);
            float rot = rng.NextFloat(0f, math.PI * 2f);
            float chosenRadius = math.min(maxRadius, math.max(35f, config.HomePlanetDistance * 0.45f));

            for (int ri = 0; ri <= 40; ri++)
            {
                float r = maxRadius * (ri + 1f) / 41f;
                var candidate = BuildRegularHomePolygon(teamCount, r, rot);
                bool ok = MeetsMinToroidalPairSeparation(candidate, mapWidth, mapHeight, minSep);
                candidate.Dispose();
                if (ok)
                {
                    chosenRadius = r;
                    break;
                }
            }

            var layout = BuildRegularHomePolygon(teamCount, chosenRadius, rot);
            for (int i = 0; i < layout.Length; i++)
                output.Add(layout[i]);
            layout.Dispose();
        }

        static NativeList<float3> BuildRegularHomePolygon(int n, float radius, float rotationRad, Allocator allocator = Allocator.Temp)
        {
            var positions = new NativeList<float3>(n, allocator);
            for (int i = 0; i < n; i++)
            {
                float ang = rotationRad + (math.PI * 2f * i) / n;
                positions.Add(new float3(math.cos(ang) * radius, 0f, math.sin(ang) * radius));
            }
            return positions;
        }

        static bool MeetsMinToroidalPairSeparation(NativeList<float3> positions, float mapW, float mapH, float minSep)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                for (int j = i + 1; j < positions.Length; j++)
                {
                    if (ToroidalMapEcs.ToroidalDistance(positions[i], positions[j], mapW, mapH) < minSep)
                        return false;
                }
            }
            return true;
        }

        static float ScoreToroidalEquidistance(NativeList<float3> positions, float mapW, float mapH)
        {
            float minD = float.MaxValue;
            float maxD = 0f;
            float sumD = 0f;
            int pairs = 0;

            for (int i = 0; i < positions.Length; i++)
            {
                for (int j = i + 1; j < positions.Length; j++)
                {
                    float d = ToroidalMapEcs.ToroidalDistance(positions[i], positions[j], mapW, mapH);
                    minD = math.min(minD, d);
                    maxD = math.max(maxD, d);
                    sumD += d;
                    pairs++;
                }
            }

            if (pairs == 0)
                return float.NegativeInfinity;

            float mean = sumD / pairs;
            float spread = maxD - minD;
            float spreadRatio = spread / math.max(1f, mean);
            return minD * 2f - spread - spreadRatio * 10f;
        }

        static float GetMaxHomePlanetRingRadius(in MapGenerationConfig config, float mapWidth, float mapHeight, float homeInfluence)
        {
            float margin = math.max(
                28f,
                math.max(
                    config.ClearanceRadiusAroundHomePlanet + 20f,
                    math.max(config.MinHomePlanetPairSeparation * 0.35f, homeInfluence + 8f)));
            float halfSpace = math.min(mapWidth, mapHeight) * 0.5f - margin;
            if (halfSpace < 20f)
                halfSpace = math.max(15f, math.min(mapWidth, mapHeight) * 0.5f - 10f);
            return math.max(20f, halfSpace);
        }

        static float3 GetRandomMapPositionAvoidingPlanetRings(
            in MapGenerationConfig config,
            float mapWidth,
            float mapHeight,
            NativeList<PlanetPlacement> planetPlacements,
            float candidateInfluenceRadius,
            ref Random rng,
            int maxAttempts = 250)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                float3 pos = new float3(
                    rng.NextFloat(-mapWidth * 0.5f, mapWidth * 0.5f),
                    0f,
                    rng.NextFloat(-mapHeight * 0.5f, mapHeight * 0.5f));
                if (!OverlapsPlanetOrbitRings(config, planetPlacements, mapWidth, mapHeight, pos, candidateInfluenceRadius))
                    return pos;
            }

            return new float3(
                rng.NextFloat(-mapWidth * 0.5f, mapWidth * 0.5f),
                0f,
                rng.NextFloat(-mapHeight * 0.5f, mapHeight * 0.5f));
        }

        static bool OverlapsPlanetOrbitRings(
            in MapGenerationConfig config,
            NativeList<PlanetPlacement> planetPlacements,
            float mapWidth,
            float mapHeight,
            float3 position,
            float bodyRadius)
        {
            float margin = math.max(0f, config.PlanetRingPlacementMargin);
            for (int i = 0; i < planetPlacements.Length; i++)
            {
                var placement = planetPlacements[i];
                float minDist = placement.InfluenceRadius + bodyRadius + margin;
                if (ToroidalMapEcs.ToroidalDistance(position, placement.Position, mapWidth, mapHeight) < minDist)
                    return true;
            }
            return false;
        }

        static NativeArray<float3> PickAsteroidClusterCenters(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            NativeList<PlanetPlacement> planetPlacements,
            int clusterCount,
            ref Random rng,
            Allocator allocator = Allocator.Temp)
        {
            var centers = new NativeArray<float3>(clusterCount, allocator);
            float halfW = rolled.MapWidth * 0.5f;
            float halfH = rolled.MapHeight * 0.5f;
            float sectorWidth = math.PI * 2f / math.max(1, clusterCount);
            float sectorJitter = sectorWidth * 0.85f;

            for (int c = 0; c < clusterCount; c++)
            {
                float sectorStart = sectorWidth * c;
                float3 chosen = float3.zero;
                bool found = false;

                for (int attempt = 0; attempt < 200; attempt++)
                {
                    float angle = sectorStart + rng.NextFloat(0f, sectorJitter);
                    float radial = rng.NextFloat(0.12f, 0.88f);
                    float3 candidate = new float3(
                        math.cos(angle) * radial * halfW,
                        0f,
                        math.sin(angle) * radial * halfH);
                    if (!OverlapsPlanetOrbitRings(config, planetPlacements, rolled.MapWidth, rolled.MapHeight, candidate, MaxAsteroidRadius))
                    {
                        chosen = candidate;
                        found = true;
                        break;
                    }
                }

                centers[c] = found
                    ? chosen
                    : GetRandomMapPositionAvoidingPlanetRings(
                        config, rolled.MapWidth, rolled.MapHeight, planetPlacements, MaxAsteroidRadius, ref rng);
            }

            return centers;
        }

        static float3 GetPositionInCluster(float3 center, int targetClusterCount, ref Random rng)
        {
            float coreRadius = math.clamp(8f + math.sqrt(math.max(1, targetClusterCount)) * 2.8f, 9f, 28f);
            float radius = coreRadius * math.pow(rng.NextFloat(), 1.15f);
            if (rng.NextFloat() < 0.25f)
                radius += coreRadius * rng.NextFloat(0.4f, 1.1f);
            float angle = rng.NextFloat(0f, math.PI * 2f);
            return center + new float3(math.cos(angle) * radius, 0f, math.sin(angle) * radius);
        }

        static bool IsTooCloseToAny(float3 pos, float minDist, NativeList<float3> positions)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                float3 delta = pos - positions[i];
                if (math.lengthsq(new float2(delta.x, delta.z)) < minDist * minDist)
                    return true;
            }
            return false;
        }
    }
}
