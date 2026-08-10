using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Shared match layout from seed + <see cref="MapGenerationConfig"/>.
    /// Server map generation and client seed-hydrate both call this so planet/asteroid poses match
    /// without streaming every body through GhostSpawn Instantiates.
    /// <para>
    /// RNG contract (must stay identical on both sides):
    /// 1. <see cref="MapGenerationLogic.RollParameters"/> from seed (separate Random).
    /// 2. Fresh <c>Random.CreateFromIndex(seed)</c> for placement.
    /// 3. Homes → neutrals → starting-claim order → per-neutral ship-family rolls → asteroids.
    /// </para>
    /// </summary>
    public static class MapLayoutBlueprint
    {
        /// <summary>One body ready for Instantiates (server ghost or client local).</summary>
        public struct Body
        {
            /// <summary>1=home, 2=neutral, 3=asteroid (same as <see cref="MapLayoutEntryElement.EntityKind"/>).</summary>
            public byte EntityKind;

            /// <summary>World pose (Y should be 0).</summary>
            public float3 Position;

            /// <summary>Uniform visual/collider scale for planets; cmax of layout scale for asteroids.</summary>
            public float Scale;

            /// <summary>Non-uniform asteroid layout scale (ignored for planets).</summary>
            public float3 AsteroidScale;

            /// <summary>Ownership at spawn (homes owned; neutrals None until claims applied).</summary>
            public TeamId Team;

            /// <summary>PlanetId for homes/neutrals; 0 for asteroids.</summary>
            public int PlanetId;

            /// <summary>Planet level (homes/neutrals).</summary>
            public int Level;

            /// <summary>Ship family slot for neutrals (0 for homes/asteroids).</summary>
            public byte ShipFamilyConfigIndex;

            /// <summary>Asteroid designer Size.</summary>
            public float Size;

            /// <summary>Asteroid gem capacity.</summary>
            public float GemValue;

            /// <summary>Asteroid max Health.</summary>
            public float MaxHealth;
        }

        /// <summary>Starting ownership flip after neutrals spawn (same order as server claims).</summary>
        public struct Claim
        {
            /// <summary>Index into the neutral body list (0..neutralCount-1), not PlanetId.</summary>
            public int NeutralLayoutIndex;

            /// <summary>Team that receives the planet.</summary>
            public TeamId Team;
        }

        /// <summary>
        /// Builds the full body list + claim order from recipe inputs.
        /// Caller owns and must Dispose <paramref name="bodies"/> / <paramref name="claims"/>.
        /// </summary>
        public static void Build(
            in MapGenerationConfig config,
            uint matchSeed,
            in MapGenerationLogic.AsteroidBodyTuning asteroidBody,
            Allocator allocator,
            out MapGenerationLogic.RolledParameters rolled,
            out NativeList<Body> bodies,
            out NativeList<Claim> claims)
        {
            // --- Roll match parameters (independent RNG from placement) ---
            rolled = MapGenerationLogic.RollParameters(config, matchSeed);

            // --- Placement RNG restarts from the same seed (server BeginGeneration contract) ---
            var rng = Random.CreateFromIndex(matchSeed);

            int estimated = rolled.TeamCount + rolled.NeutralPlanetCount + rolled.AsteroidCount;
            bodies = new NativeList<Body>(math.max(16, estimated), allocator);
            claims = new NativeList<Claim>(
                math.max(8, config.StartingOwnedNeutralPlanetsPerTeam * rolled.TeamCount),
                allocator);

            var homeLayouts = new NativeList<MapGenerationLogic.HomePlanetLayout>(rolled.TeamCount, Allocator.Temp);
            var neutralLayouts = new NativeList<MapGenerationLogic.NeutralPlanetLayout>(
                rolled.NeutralPlanetCount, Allocator.Temp);
            var asteroidLayouts = new NativeList<MapGenerationLogic.AsteroidLayout>(
                rolled.AsteroidCount, Allocator.Temp);
            var planetPlacements = new NativeList<MapGenerationLogic.PlanetPlacement>(
                math.max(16, estimated), Allocator.Temp);

            // --- Homes ---
            MapGenerationLogic.BuildHomePlanets(config, rolled, ref rng, homeLayouts, planetPlacements);
            for (int i = 0; i < homeLayouts.Length; i++)
            {
                var home = homeLayouts[i];
                var team = (TeamId)(i + 1);
                bodies.Add(new Body
                {
                    EntityKind = 1,
                    Position = home.Position,
                    Scale = home.Scale,
                    AsteroidScale = new float3(home.Scale),
                    Team = team,
                    PlanetId = (int)team,
                    Level = home.Level,
                    ShipFamilyConfigIndex = 0,
                });
            }

            // --- Neutrals (unowned at spawn) ---
            MapGenerationLogic.BuildNeutralPlanets(config, rolled, ref rng, planetPlacements, neutralLayouts);

            // --- Starting claim order (consumes RNG before ship-family rolls) ---
            var claimWork = new NativeList<MapGenerationLogic.StartingNeutralClaim>(
                math.max(8, config.StartingOwnedNeutralPlanetsPerTeam * rolled.TeamCount),
                Allocator.Temp);
            var homePositions = new NativeArray<float3>(rolled.TeamCount, Allocator.Temp);
            for (int i = 0; i < homeLayouts.Length && i < homePositions.Length; i++)
                homePositions[i] = homeLayouts[i].Position;

            MapGenerationLogic.BuildStartingNeutralClaimOrder(
                config.StartingOwnedNeutralPlanetsPerTeam,
                rolled.TeamCount,
                homePositions,
                neutralLayouts,
                rolled.MapWidth,
                rolled.MapHeight,
                ref rng,
                ref claimWork);
            homePositions.Dispose();

            for (int i = 0; i < claimWork.Length; i++)
            {
                claims.Add(new Claim
                {
                    NeutralLayoutIndex = claimWork[i].NeutralLayoutIndex,
                    Team = claimWork[i].Team,
                });
            }

            claimWork.Dispose();

            // --- Per-neutral ship-family rolls (same order as MapGenerationSystem spawn queue) ---
            int nextNeutralPlanetId = 100;
            for (int i = 0; i < neutralLayouts.Length; i++)
            {
                var neutral = neutralLayouts[i];
                byte family = (byte)(1 + rng.NextInt(0, PlanetShipFamilyAssignment.NonHomeFamilySlotCount));
                bodies.Add(new Body
                {
                    EntityKind = 2,
                    Position = neutral.Position,
                    Scale = neutral.Scale,
                    AsteroidScale = new float3(neutral.Scale),
                    Team = TeamId.None,
                    PlanetId = nextNeutralPlanetId++,
                    Level = neutral.Level,
                    ShipFamilyConfigIndex = family,
                });
            }

            // --- Asteroids (may underfill vs rolled count) ---
            MapGenerationLogic.BuildAsteroids(
                config, rolled, asteroidBody, ref rng, planetPlacements, asteroidLayouts);
            for (int i = 0; i < asteroidLayouts.Length; i++)
            {
                var asteroid = asteroidLayouts[i];
                bodies.Add(new Body
                {
                    EntityKind = 3,
                    Position = asteroid.Position,
                    Scale = math.cmax(asteroid.Scale),
                    AsteroidScale = asteroid.Scale,
                    Team = TeamId.None,
                    PlanetId = 0,
                    Level = 0,
                    ShipFamilyConfigIndex = 0,
                    Size = asteroid.Size,
                    GemValue = asteroid.GemValue,
                    MaxHealth = asteroid.MaxHealth,
                });
            }

            homeLayouts.Dispose();
            neutralLayouts.Dispose();
            asteroidLayouts.Dispose();
            planetPlacements.Dispose();
        }
    }
}
