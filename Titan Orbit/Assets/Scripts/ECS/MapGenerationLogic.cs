using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>Seed-based procedural layout for ECS map generation (ported from legacy MapGenerator).
    /// Pure functions — no EntityManager. Called by <see cref="MapGenerationSystem"/> on the server.</summary>
    public static class MapGenerationLogic
    {
        /// <summary>Minimum teams supported by home-planet polygon placement.</summary>
        public const int MinSupportedTeams = 2;

        /// <summary>Maximum teams supported by home-planet polygon placement.</summary>
        public const int MaxSupportedTeams = 5;

        /// <summary>Home planets use a larger gem-moon influence radius for map spacing math.</summary>
        public const float HomeGemMoonScaleMultiplier = 1.5f;

        const float MinAsteroidRadius = 0.35f;
        const float MaxAsteroidRadius = MinAsteroidRadius * 10f;

        /// <summary>One planet's world position and clearance ring for overlap tests during placement.</summary>
        public struct PlanetPlacement
        {
            public float3 Position;
            /// <summary>World units — orbit rings and neutral placement avoid this radius.</summary>
            public float InfluenceRadius;
        }

        /// <summary>Random draw for one match — produced once in <see cref="RollParameters"/>.</summary>
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

        /// <summary>Spawn layout for one team's home planet before entity instantiation.</summary>
        public struct HomePlanetLayout
        {
            public float3 Position;
            public float Scale;
            public int Level;
        }

        /// <summary>Spawn layout for one neutral planet before entity instantiation.</summary>
        public struct NeutralPlanetLayout
        {
            public float3 Position;
            public float Scale;
            public int Level;
        }

        /// <summary>Spawn layout for one asteroid — scale is non-uniform for visual variety.</summary>
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

        /// <summary>
        /// Rolls map size, team count, neutral planet count, and asteroid counts from config bounds.
        /// Uses config.Seed when non-zero; otherwise uses the ephemeral fallback from the caller.
        /// </summary>
        public static RolledParameters RollParameters(in MapGenerationConfig config, uint fallbackSeed)
        {
            // --- Seed and RNG ---
            uint seed = config.Seed != 0 ? (uint)config.Seed : fallbackSeed;
            var rng = Random.CreateFromIndex(seed);

            // --- Map dimensions (square map — width equals height) ---
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

        /// <summary>
        /// Places home planets on a toroidal map as a regular polygon with separation scoring.
        /// Clears and fills <paramref name="output"/> and seeds <paramref name="planetPlacements"/>.
        /// </summary>
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

        /// <summary>
        /// Places neutral planets avoiding existing planet influence rings; randomizes starting level
        /// when configured. Appends to <paramref name="planetPlacements"/> for asteroid placement.
        /// </summary>
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

        /// <summary>
        /// Fills asteroid clusters around sector-sampled centers; gem value scales visual size.
        /// Retries placement so we reach the rolled target count even when planet rings are dense.
        /// </summary>
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
            float minSpacing = math.max(0.25f, config.MinAsteroidSpacing);

            // --- Primary pass: place near cluster centers ---
            // [TITAN-ORBIT] Each slot gets several attempts; a single overlap used to skip the slot
            // and under-spawn (e.g. ~82 instead of the rolled 444–888 target).
            for (int c = 0; c < rolled.AsteroidClusterCount && output.Length < rolled.AsteroidCount; c++)
            {
                float3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && output.Length < rolled.AsteroidCount; i++)
                {
                    if (!TryPlaceAsteroidNearCenter(
                            config, rolled, planetPlacements, asteroidPositions, center, perCluster,
                            minSpacing, gemLo, gemHi, gemSpan, ref rng, output))
                    {
                        // Keep trying other clusters; fill pass below covers shortfall.
                    }
                }
            }

            // --- Fill pass: any remaining slots anywhere on the map ---
            // [TITAN-ORBIT] Guarantees we hit RolledParameters.AsteroidCount when space exists.
            const int fillAttemptsPerSlot = 40;
            while (output.Length < rolled.AsteroidCount)
            {
                bool placed = false;
                for (int attempt = 0; attempt < fillAttemptsPerSlot; attempt++)
                {
                    float3 position = new float3(
                        rng.NextFloat(-rolled.MapWidth * 0.5f, rolled.MapWidth * 0.5f),
                        0f,
                        rng.NextFloat(-rolled.MapHeight * 0.5f, rolled.MapHeight * 0.5f));
                    if (IsTooCloseToAny(position, minSpacing, asteroidPositions))
                        continue;
                    if (OverlapsPlanetOrbitRings(
                            config, planetPlacements, rolled.MapWidth, rolled.MapHeight, position, MaxAsteroidRadius))
                        continue;

                    AppendAsteroidLayout(position, gemLo, gemHi, gemSpan, ref rng, asteroidPositions, output);
                    placed = true;
                    break;
                }

                if (!placed)
                    break; // Map too full — publish whatever we placed.
            }

            clusterCenters.Dispose();
            asteroidPositions.Dispose();
        }

        /// <summary>
        /// Tries several cluster-local positions until one clears spacing and planet rings.
        /// </summary>
        static bool TryPlaceAsteroidNearCenter(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            NativeList<PlanetPlacement> planetPlacements,
            NativeList<float3> asteroidPositions,
            float3 center,
            int perCluster,
            float minSpacing,
            float gemLo,
            float gemHi,
            float gemSpan,
            ref Random rng,
            NativeList<AsteroidLayout> output)
        {
            const int attemptsPerSlot = 24;
            for (int attempt = 0; attempt < attemptsPerSlot; attempt++)
            {
                float3 position = GetPositionInCluster(center, perCluster, ref rng);
                if (IsTooCloseToAny(position, minSpacing, asteroidPositions))
                    continue;
                if (OverlapsPlanetOrbitRings(
                        config, planetPlacements, rolled.MapWidth, rolled.MapHeight, position, MaxAsteroidRadius))
                    continue;

                AppendAsteroidLayout(position, gemLo, gemHi, gemSpan, ref rng, asteroidPositions, output);
                return true;
            }

            return false;
        }

        /// <summary>Writes one asteroid layout entry and records its position for spacing tests.</summary>
        static void AppendAsteroidLayout(
            float3 position,
            float gemLo,
            float gemHi,
            float gemSpan,
            ref Random rng,
            NativeList<float3> asteroidPositions,
            NativeList<AsteroidLayout> output)
        {
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

        /// <summary>
        /// Picks starting ownership for non-home planets so each team gets up to
        /// <paramref name="desiredPerTeam"/> owned neutrals.
        /// <para>
        /// If there are not enough neutrals for every team to reach the desired count, ownership is
        /// spread as evenly as possible (e.g. want 4×4 but only 12 neutrals → 3 each). Any leftover
        /// after the even floor is given to randomly chosen teams that currently have fewer.
        /// Remaining planets stay <see cref="TeamId.None"/>.
        /// </para>
        /// </summary>
        /// <param name="desiredPerTeam">Designer setting (0 disables pre-ownership).</param>
        /// <param name="teamCount">Active teams this match (2–5).</param>
        /// <param name="neutralCount">How many non-home planets were placed.</param>
        /// <param name="rng">Match RNG — used to shuffle which planets/teams get leftovers.</param>
        /// <param name="outOwnership">Length = neutralCount; filled with TeamA..E or None.</param>
        public static void AssignStartingOwnedNeutralTeams(
            int desiredPerTeam,
            int teamCount,
            int neutralCount,
            ref Random rng,
            NativeArray<TeamId> outOwnership)
        {
            // --- Clear to neutral ---
            for (int i = 0; i < outOwnership.Length; i++)
                outOwnership[i] = TeamId.None;

            if (desiredPerTeam <= 0 || teamCount < MinSupportedTeams || neutralCount <= 0)
                return;

            teamCount = math.clamp(teamCount, MinSupportedTeams, MaxSupportedTeams);
            if (outOwnership.Length < neutralCount)
                neutralCount = outOwnership.Length;

            // --- How many we can actually assign ---
            // Cap at available neutrals; then split evenly across teams.
            int totalDesired = desiredPerTeam * teamCount;
            int totalAssign = math.min(totalDesired, neutralCount);
            if (totalAssign <= 0)
                return;

            int baseEach = totalAssign / teamCount;
            int remainder = totalAssign % teamCount;

            // counts[t] = how many neutrals team (t+1) receives.
            var counts = new NativeArray<int>(teamCount, Allocator.Temp);
            for (int t = 0; t < teamCount; t++)
                counts[t] = baseEach;

            // --- Uneven leftovers → random teams that currently have fewer ---
            // [TITAN-ORBIT] Fisher–Yates on team indices; first `remainder` teams get +1.
            if (remainder > 0)
            {
                var teamOrder = new NativeArray<int>(teamCount, Allocator.Temp);
                for (int t = 0; t < teamCount; t++)
                    teamOrder[t] = t;
                for (int i = teamCount - 1; i > 0; i--)
                {
                    int j = rng.NextInt(0, i + 1);
                    (teamOrder[i], teamOrder[j]) = (teamOrder[j], teamOrder[i]);
                }

                for (int r = 0; r < remainder; r++)
                    counts[teamOrder[r]]++;

                teamOrder.Dispose();
            }

            // --- Build shuffled ownership slots, then assign to shuffled planet indices ---
            var slots = new NativeList<TeamId>(totalAssign, Allocator.Temp);
            for (int t = 0; t < teamCount; t++)
            {
                var team = (TeamId)(t + 1);
                for (int n = 0; n < counts[t]; n++)
                    slots.Add(team);
            }

            for (int i = slots.Length - 1; i > 0; i--)
            {
                int j = rng.NextInt(0, i + 1);
                (slots[i], slots[j]) = (slots[j], slots[i]);
            }

            var planetOrder = new NativeArray<int>(neutralCount, Allocator.Temp);
            for (int i = 0; i < neutralCount; i++)
                planetOrder[i] = i;
            for (int i = neutralCount - 1; i > 0; i--)
            {
                int j = rng.NextInt(0, i + 1);
                (planetOrder[i], planetOrder[j]) = (planetOrder[j], planetOrder[i]);
            }

            for (int i = 0; i < slots.Length; i++)
                outOwnership[planetOrder[i]] = slots[i];

            counts.Dispose();
            slots.Dispose();
            planetOrder.Dispose();
        }
    }
}
