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

        /// <summary>
        /// Burst-safe asteroid body tuning snapshot (copied from <c>AsteroidSettings</c> before layout).
        /// Size is a designer unit; visual scale and HP/gems are derived from the ratios.
        /// </summary>
        public struct AsteroidBodyTuning
        {
            /// <summary>Rolled Size lower bound.</summary>
            public float MinSize;

            /// <summary>Rolled Size upper bound.</summary>
            public float MaxSize;

            /// <summary>Health Cap = Size × this.</summary>
            public float HealthPerSize;

            /// <summary>Gem capacity = Size × this.</summary>
            public float GemsPerSize;

            /// <summary>Uniform mesh scale at MinSize (before jitter).</summary>
            public float VisualScaleAtMinSize;

            /// <summary>Uniform mesh scale at MaxSize (before jitter).</summary>
            public float VisualScaleAtMaxSize;

            /// <summary>Largest visual scale — used for planet-ring overlap clearance.</summary>
            public float MaxVisualRadius => math.max(VisualScaleAtMinSize, VisualScaleAtMaxSize);
        }

        /// <summary>Legacy defaults when no <c>AsteroidSettings</c> asset is loaded (size≈old gem range).</summary>
        public static AsteroidBodyTuning DefaultAsteroidBodyTuning => new AsteroidBodyTuning
        {
            MinSize = 1f,
            MaxSize = 70f,
            HealthPerSize = 1f,
            GemsPerSize = 1f,
            VisualScaleAtMinSize = 0.35f,
            VisualScaleAtMaxSize = 3.5f,
        };

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
            public float MapRadius;
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

        /// <summary>
        /// Spawn layout for one asteroid — scale is non-uniform for visual variety.
        /// <see cref="GemValue"/> and <see cref="MaxHealth"/> come from designer Size × ratios
        /// in <see cref="AsteroidBodyTuning"/> (not 1:1 with each other unless ratios match).
        /// </summary>
        public struct AsteroidLayout
        {
            public float3 Position;
            public float3 Scale;
            public float Size;
            public float GemValue;
            public float MaxHealth;
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
                MapRadius = SphericalMapEcs.RadiusFromMapSize(mapSize),
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
        /// Fills asteroid clusters around sector-sampled centers.
        /// Size (from <paramref name="body"/>) drives visual scale, HP, and gems via ratios.
        /// Retries placement so we reach the rolled target count even when planet rings are dense.
        /// </summary>
        public static void BuildAsteroids(
            in MapGenerationConfig config,
            in RolledParameters rolled,
            in AsteroidBodyTuning body,
            ref Random rng,
            NativeList<PlanetPlacement> planetPlacements,
            NativeList<AsteroidLayout> output)
        {
            output.Clear();
            if (rolled.AsteroidCount <= 0 || rolled.AsteroidClusterCount <= 0)
                return;

            var asteroidPositions = new NativeList<float3>(rolled.AsteroidCount, Allocator.Temp);
            int perCluster = (int)math.ceil((float)rolled.AsteroidCount / math.max(1, rolled.AsteroidClusterCount));
            float minSpacing = math.max(0.25f, config.MinAsteroidSpacing);
            float clearanceRadius = math.max(0.1f, body.MaxVisualRadius);
            var clusterCenters = PickAsteroidClusterCenters(
                config, rolled, planetPlacements, rolled.AsteroidClusterCount, clearanceRadius, ref rng);

            // --- Primary pass: place near cluster centers ---
            // [TITAN-ORBIT] Each slot gets several attempts; a single overlap used to skip the slot
            // and under-spawn (e.g. ~82 instead of the rolled 444–888 target).
            for (int c = 0; c < rolled.AsteroidClusterCount && output.Length < rolled.AsteroidCount; c++)
            {
                float3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && output.Length < rolled.AsteroidCount; i++)
                {
                    if (!TryPlaceAsteroidNearCenter(
                            config, rolled, body, planetPlacements, asteroidPositions, center, perCluster,
                            minSpacing, clearanceRadius, ref rng, output))
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
                    float3 position = RandomShellPosition(rolled.MapWidth, rolled.MapHeight, ref rng);
                    if (IsTooCloseToAny(position, minSpacing, asteroidPositions))
                        continue;
                    if (OverlapsPlanetOrbitRings(
                            config, planetPlacements, rolled.MapWidth, rolled.MapHeight, position, clearanceRadius))
                        continue;

                    AppendAsteroidLayout(position, body, ref rng, asteroidPositions, output);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    // --- Density failure ---
                    // [TITAN-ORBIT] Small map + large planet rings / clearance can exhaust free
                    // tiles before we hit RolledParameters.AsteroidCount. Callers must publish
                    // output.Length (actual) as LoadingTotalSteps — never the rolled target —
                    // or the client loading bar hangs below the 92% Join Team gate.
                    UnityEngine.Debug.LogWarning(
                        "[MapGenerationLogic] Asteroid fill aborted — map too full. " +
                        "placed=" + output.Length + "/" + rolled.AsteroidCount +
                        " map=" + rolled.MapWidth.ToString("F0") + "x" + rolled.MapHeight.ToString("F0") +
                        " planets=" + planetPlacements.Length +
                        " minSpacing=" + minSpacing.ToString("F2") +
                        " clearance=" + clearanceRadius.ToString("F2") +
                        " seed=" + rolled.Seed);
                    break;
                }
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
            in AsteroidBodyTuning body,
            NativeList<PlanetPlacement> planetPlacements,
            NativeList<float3> asteroidPositions,
            float3 center,
            int perCluster,
            float minSpacing,
            float clearanceRadius,
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
                        config, planetPlacements, rolled.MapWidth, rolled.MapHeight, position, clearanceRadius))
                    continue;

                AppendAsteroidLayout(position, body, ref rng, asteroidPositions, output);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Rolls designer Size, derives gems/HP/visual scale from <paramref name="body"/> ratios,
        /// applies per-axis jitter, and records the position for spacing tests.
        /// </summary>
        static void AppendAsteroidLayout(
            float3 position,
            in AsteroidBodyTuning body,
            ref Random rng,
            NativeList<float3> asteroidPositions,
            NativeList<AsteroidLayout> output)
        {
            asteroidPositions.Add(position);

            // --- Designer Size first (drives HP, gems, and visual scale) ---
            float sizeLo = math.min(body.MinSize, body.MaxSize);
            float sizeHi = math.max(body.MinSize, body.MaxSize);
            float size = rng.NextFloat(sizeLo, sizeHi);
            float sizeSpan = math.max(0.001f, sizeHi - sizeLo);
            float t = math.saturate((size - sizeLo) / sizeSpan);

            float gemValue = math.max(0.25f, size * math.max(0f, body.GemsPerSize));
            float maxHealth = math.max(1f, size * math.max(0.01f, body.HealthPerSize));
            float linearScale = math.lerp(body.VisualScaleAtMinSize, body.VisualScaleAtMaxSize, t);

            // Slight non-uniform mesh so rocks do not look identical.
            float3 scale = new float3(
                linearScale * (0.8f + rng.NextFloat() * 0.4f),
                linearScale * (0.9f + rng.NextFloat() * 0.2f),
                linearScale * (0.85f + rng.NextFloat() * 0.3f));

            output.Add(new AsteroidLayout
            {
                Position = position,
                Scale = scale,
                Size = size,
                GemValue = gemValue,
                MaxHealth = maxHealth,
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
                        var candidate = BuildRegularHomePolygon(teamCount, r, rot, mapWidth, mapHeight);
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
                var candidate = BuildRegularHomePolygon(teamCount, r, rot, mapWidth, mapHeight);
                bool ok = MeetsMinToroidalPairSeparation(candidate, mapWidth, mapHeight, minSep);
                candidate.Dispose();
                if (ok)
                {
                    chosenRadius = r;
                    break;
                }
            }

            var layout = BuildRegularHomePolygon(teamCount, chosenRadius, rot, mapWidth, mapHeight);
            for (int i = 0; i < layout.Length; i++)
                output.Add(layout[i]);
            layout.Dispose();
        }

        static NativeList<float3> BuildRegularHomePolygon(
            int n,
            float radius,
            float rotationRad,
            float mapWidth,
            float mapHeight,
            Allocator allocator = Allocator.Temp)
        {
            _ = radius;
            var positions = new NativeList<float3>(n, allocator);
            float shell = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            for (int i = 0; i < n; i++)
            {
                float ang = rotationRad + (math.PI * 2f * i) / n;
                float3 dir = new float3(math.cos(ang), 0f, math.sin(ang));
                positions.Add(SphericalMapEcs.ProjectToSphere(dir, shell));
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
                float3 pos = RandomShellPosition(mapWidth, mapHeight, ref rng);
                if (!OverlapsPlanetOrbitRings(config, planetPlacements, mapWidth, mapHeight, pos, candidateInfluenceRadius))
                    return pos;
            }

            return RandomShellPosition(mapWidth, mapHeight, ref rng);
        }

        static float3 RandomShellPosition(float mapWidth, float mapHeight, ref Random rng)
        {
            float radius = SphericalMapEcs.RadiusFromMapAxes(mapWidth, mapHeight);
            return SphericalMapEcs.RandomUnitDirection(ref rng) * radius;
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
            float asteroidClearanceRadius,
            ref Random rng,
            Allocator allocator = Allocator.Temp)
        {
            var centers = new NativeArray<float3>(clusterCount, allocator);
            float shell = SphericalMapEcs.RadiusFromMapAxes(rolled.MapWidth, rolled.MapHeight);
            float clearance = math.max(0.1f, asteroidClearanceRadius);

            for (int c = 0; c < clusterCount; c++)
            {
                float3 mean = SphericalMapEcs.FibonacciDirection(c, clusterCount);
                float3 chosen = mean * shell;
                bool found = false;

                for (int attempt = 0; attempt < 200; attempt++)
                {
                    float3 candidate = SphericalMapEcs.VonMisesFisher(mean, 4f, ref rng) * shell;
                    if (!OverlapsPlanetOrbitRings(config, planetPlacements, rolled.MapWidth, rolled.MapHeight, candidate, clearance))
                    {
                        chosen = candidate;
                        found = true;
                        break;
                    }
                }

                centers[c] = found
                    ? chosen
                    : GetRandomMapPositionAvoidingPlanetRings(
                        config, rolled.MapWidth, rolled.MapHeight, planetPlacements, clearance, ref rng);
            }

            return centers;
        }

        static float3 GetPositionInCluster(float3 center, int targetClusterCount, ref Random rng)
        {
            float shell = math.max(1f, math.length(center));
            float coreRadius = math.clamp(8f + math.sqrt(math.max(1, targetClusterCount)) * 2.8f, 9f, 28f);
            float arc = coreRadius * math.pow(rng.NextFloat(), 1.15f);
            if (rng.NextFloat() < 0.25f)
                arc += coreRadius * rng.NextFloat(0.4f, 1.1f);
            float kappa = math.max(2f, (shell / math.max(1f, arc)) * 0.85f);
            return SphericalMapEcs.VonMisesFisher(math.normalizesafe(center, new float3(0f, 1f, 0f)), kappa, ref rng) * shell;
        }

        static bool IsTooCloseToAny(float3 pos, float minDist, NativeList<float3> positions)
        {
            float radius = math.max(1f, math.length(pos));
            for (int i = 0; i < positions.Length; i++)
            {
                if (SphericalMapEcs.GeodesicDistance(pos, positions[i], radius) < minDist)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// One deferred starting capture: which neutral layout index a team will own, in deal order.
        /// Applied one-at-a-time during map generation so sticky planet connections can rebuild
        /// between captures (mimics players capturing over time).
        /// </summary>
        public struct StartingNeutralClaim
        {
            /// <summary>Index into the neutral layout list from <see cref="BuildNeutralPlanets"/>.</summary>
            public int NeutralLayoutIndex;

            /// <summary>Team that will receive this planet when the claim is applied.</summary>
            public TeamId Team;
        }

        /// <summary>
        /// Builds a round-robin starting-capture order so each team gets one neutral at a time
        /// before the next round (TeamA, TeamB, … then TeamA again).
        /// <para>
        /// [TITAN-ORBIT] On each team's turn they “choose” the closest still-available neutral to
        /// their home (toroidal distance). Totals stay even across teams when neutrals are scarce
        /// (same floor/remainder as the old instant assign). Leftover neutrals stay unowned.
        /// </para>
        /// Callers spawn neutrals as <see cref="TeamId.None"/> first, then apply each claim over
        /// successive sim ticks so <c>PlanetConnectionGraphSystem</c> can rebuild sticky edges
        /// between captures (avoids wiring every pre-owned planet in one fingerprint update).
        /// </summary>
        /// <param name="desiredPerTeam">Designer setting (0 disables pre-ownership).</param>
        /// <param name="teamCount">Active teams this match (2–5).</param>
        /// <param name="homePositions">Home world XZ per team index 0..teamCount-1 (TeamA = 0).</param>
        /// <param name="neutrals">Placed neutral layouts (positions used for closest-pick).</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="rng">Match RNG — used only for remainder team picks when counts are uneven.</param>
        /// <param name="outClaims">Cleared then filled in deal order (round-robin).</param>
        public static void BuildStartingNeutralClaimOrder(
            int desiredPerTeam,
            int teamCount,
            in NativeArray<float3> homePositions,
            in NativeList<NeutralPlanetLayout> neutrals,
            float mapW,
            float mapH,
            ref Random rng,
            ref NativeList<StartingNeutralClaim> outClaims)
        {
            outClaims.Clear();

            if (desiredPerTeam <= 0 ||
                teamCount < MinSupportedTeams ||
                !neutrals.IsCreated ||
                neutrals.Length <= 0 ||
                !homePositions.IsCreated ||
                homePositions.Length < teamCount)
                return;

            teamCount = math.clamp(teamCount, MinSupportedTeams, MaxSupportedTeams);
            int neutralCount = neutrals.Length;

            // --- How many each team should receive (even floor + random remainder) ---
            int totalDesired = desiredPerTeam * teamCount;
            int totalAssign = math.min(totalDesired, neutralCount);
            if (totalAssign <= 0)
                return;

            int baseEach = totalAssign / teamCount;
            int remainder = totalAssign % teamCount;

            var remaining = new NativeArray<int>(teamCount, Allocator.Temp);
            for (int t = 0; t < teamCount; t++)
                remaining[t] = baseEach;

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
                    remaining[teamOrder[r]]++;

                teamOrder.Dispose();
            }

            // --- Available neutral indices (true until claimed in this deal) ---
            var available = new NativeList<int>(neutralCount, Allocator.Temp);
            for (int i = 0; i < neutralCount; i++)
                available.Add(i);

            // --- Round-robin deal: one planet per team per pass ---
            // [TITAN-ORBIT] Closest-to-home pick mimics expanding from the homeworld first.
            bool anyLeft = true;
            while (anyLeft && available.Length > 0)
            {
                anyLeft = false;
                for (int t = 0; t < teamCount; t++)
                {
                    if (remaining[t] <= 0 || available.Length == 0)
                        continue;

                    anyLeft = true;
                    float3 homePos = homePositions[t];
                    int bestAvailSlot = 0;
                    float bestDist = float.MaxValue;
                    for (int a = 0; a < available.Length; a++)
                    {
                        int nIdx = available[a];
                        float d = ToroidalMapEcs.ToroidalDistance(
                            homePos, neutrals[nIdx].Position, mapW, mapH);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestAvailSlot = a;
                        }
                    }

                    int chosen = available[bestAvailSlot];
                    available.RemoveAtSwapBack(bestAvailSlot);
                    remaining[t]--;

                    outClaims.Add(new StartingNeutralClaim
                    {
                        NeutralLayoutIndex = chosen,
                        Team = (TeamId)(t + 1),
                    });
                }
            }

            remaining.Dispose();
            available.Dispose();
        }
    }
}
