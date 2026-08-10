using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Collections;
// ToroidalMap / ToroidalMapEcs live in TitanOrbit.Generation (Shared asm).
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client builds the procedural map locally from the match seed recipe.
    /// Replaces GhostSpawn Instantiates of hundreds of planet/asteroid ghosts.
    /// <para>
    /// Pipeline: <c>MapSessionMetaRpc</c> latches recipe → this system builds bodies in budgeted
    /// batches → <see cref="ClientMapHydrateCache.IsComplete"/> →
    /// <c>TitanOrbitGoInGameClientSystem</c> may add <see cref="NetworkStreamInGame"/>.
    /// </para>
    /// World: ClientSimulation. GoInGame gates on <see cref="ClientMapHydrateCache.IsComplete"/>
    /// (cannot UpdateBefore NetCode assembly — circular asmdef).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ClientMapHydrateSystem : ISystem
    {
        /// <summary>How many local bodies to Instantiates per frame (smooth loading bar, not Crash!!!).</summary>
        public const int BodiesPerFrame = 24;

        bool _blueprintReady;
        bool _loggedWaitingPrefabs;
        NativeList<MapLayoutBlueprint.Body> _bodies;
        NativeList<MapLayoutBlueprint.Claim> _claims;
        NativeList<int> _neutralPlanetIds;
        int _bodyIndex;
        int _asteroidsSpawned;
        MapGenerationLogic.RolledParameters _rolled;

        /// <summary>Needs GamePrefabs when hydrate starts; always ticks while recipe is pending.</summary>
        public void OnCreate(ref SystemState state)
        {
            _bodies = default;
            _claims = default;
            _neutralPlanetIds = default;
        }

        /// <summary>Disposes native blueprint lists.</summary>
        public void OnDestroy(ref SystemState state)
        {
            DisposeBlueprint();
        }

        /// <summary>
        /// When a full recipe is latched and hydrate is incomplete, build local map bodies.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Already done this session ---
            if (ClientMapHydrateCache.IsComplete)
                return;

            // --- Need full seed recipe (not counts-only meta) ---
            if (!ClientMapHydrateCache.HasFullRecipe)
                return;

            var em = state.EntityManager;

            // --- Build blueprint once ---
            if (!_blueprintReady)
            {
                if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) ||
                    prefabs.Planet == Entity.Null ||
                    prefabs.Asteroid == Entity.Null)
                {
                    if (!_loggedWaitingPrefabs)
                    {
                        _loggedWaitingPrefabs = true;
                        Debug.Log(
                            "[ClientMapHydrate] Waiting for GamePrefabs (Planet/Asteroid) before seed hydrate.");
                    }

                    return;
                }

                DisposeBlueprint();
                MapLayoutBlueprint.Build(
                    ClientMapHydrateCache.RecipeConfig,
                    ClientMapHydrateCache.MatchSeed,
                    ClientMapHydrateCache.AsteroidBody,
                    Allocator.Persistent,
                    out _rolled,
                    out _bodies,
                    out _claims);

                // --- Count asteroids only ---
                // [TITAN-ORBIT] Planets still replicate as ghosts (ownership/pop/shield). Asteroids
                // are the Instantiates flood — hydrate those locally and skip planet Instantiates.
                int asteroidCount = 0;
                for (int i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i].EntityKind == 3)
                        asteroidCount++;
                }

                _neutralPlanetIds = new NativeList<int>(math.max(8, _rolled.NeutralPlanetCount), Allocator.Persistent);
                for (int i = 0; i < _bodies.Length; i++)
                {
                    if (_bodies[i].EntityKind == 2)
                        _neutralPlanetIds.Add(_bodies[i].PlanetId);
                }

                _bodyIndex = 0;
                _asteroidsSpawned = 0;
                _blueprintReady = true;
                ClientMapHydrateCache.MarkHydrateStarted(asteroidCount);

                // --- Toroidal size from rolled recipe ---
                if (ToroidalMapEcs.IsValidMapSize(_rolled.MapWidth, _rolled.MapHeight))
                {
                    ToroidalMapEcs.SetMapSize(_rolled.MapWidth, _rolled.MapHeight);
                    ToroidalMap.SetMapSize(_rolled.MapWidth, _rolled.MapHeight);
                }

                Debug.Log(
                    "[ClientMapHydrate] Blueprint ready bodies=" + _bodies.Length +
                    " claims=" + _claims.Length +
                    " seed=" + ClientMapHydrateCache.MatchSeed +
                    " map=" + _rolled.MapWidth.ToString("F0") + "x" + _rolled.MapHeight.ToString("F0"));
            }

            if (!_bodies.IsCreated)
                return;

            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var gamePrefabs))
                return;

            // --- Spawn asteroid batch (skip planet kinds — those arrive as ghosts) ---
            int spawnedThisFrame = 0;
            while (_bodyIndex < _bodies.Length && spawnedThisFrame < BodiesPerFrame)
            {
                var body = _bodies[_bodyIndex];
                _bodyIndex++;
                if (body.EntityKind != 3)
                    continue;

                ClientLocalMapBodySpawn.SpawnAsteroid(em, gamePrefabs.Asteroid, body);
                spawnedThisFrame++;
                _asteroidsSpawned++;
                ClientMapHydrateCache.SetBuiltBodies(_asteroidsSpawned);
            }

            if (_bodyIndex < _bodies.Length)
                return;

            // Claims apply on the server to planet ghosts; ownership RPCs / ghost fields cover clients.
            ClientMapHydrateCache.MarkComplete();
            DisposeBlueprint();
            _blueprintReady = false;

            Debug.Log(
                "[ClientMapHydrate] Asteroid hydrate complete built=" + ClientMapHydrateCache.BuiltBodies +
                "/" + ClientMapHydrateCache.ExpectedBodies +
                " — GoInGame may proceed.");
        }

        void DisposeBlueprint()
        {
            if (_bodies.IsCreated)
                _bodies.Dispose();
            if (_claims.IsCreated)
                _claims.Dispose();
            if (_neutralPlanetIds.IsCreated)
                _neutralPlanetIds.Dispose();
            _bodies = default;
            _claims = default;
            _neutralPlanetIds = default;
            _bodyIndex = 0;
            _asteroidsSpawned = 0;
        }
    }
}
