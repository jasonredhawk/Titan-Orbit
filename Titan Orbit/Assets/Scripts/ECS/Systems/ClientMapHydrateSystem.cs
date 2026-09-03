using TitanOrbit.Core;
using TitanOrbit.Generation;
using Unity.Collections;
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
    /// Pipeline: recipe latch (<see cref="ClientMapHydrateCache"/>) → budgeted asteroid
    /// Instantiates → <see cref="ClientMapHydrateCache.IsComplete"/> →
    /// <c>TitanOrbitGoInGameClientSystem</c> adds <see cref="NetworkStreamInGame"/>.
    /// </para>
    /// <para>
    /// Watches <see cref="ClientMapHydrateCache.SessionGeneration"/>. Disconnect / Play-again
    /// without Domain Reload used to leave <c>_blueprintReady</c> true with disposed lists,
    /// so this system returned forever and the loading bar never moved for real.
    /// </para>
    /// World: ClientSimulation. Group: SimulationSystemGroup, OrderFirst.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ClientMapHydrateSystem : ISystem
    {
        /// <summary>How many local asteroids to Instantiates per frame (smooth bar, not Crash!!!).</summary>
        public const int BodiesPerFrame = 24;

        bool _blueprintReady;
        bool _loggedWaitingPrefabs;
        float _nextPrefabWaitLogRealtime;
        int _appliedGeneration;
        NativeList<MapLayoutBlueprint.Body> _bodies;
        NativeList<MapLayoutBlueprint.Claim> _claims;
        NativeList<int> _neutralPlanetIds;
        int _bodyIndex;
        int _asteroidsSpawned;
        MapGenerationLogic.RolledParameters _rolled;

        /// <summary>Native lists start uncreated; generation is forced to mismatch so the first tick rebuilds.</summary>
        public void OnCreate(ref SystemState state)
        {
            _bodies = default;
            _claims = default;
            _neutralPlanetIds = default;
            _appliedGeneration = int.MinValue;
            _blueprintReady = false;
            _loggedWaitingPrefabs = false;
            _nextPrefabWaitLogRealtime = 0f;
        }

        /// <summary>Disposes native blueprint lists when the ClientWorld is destroyed.</summary>
        public void OnDestroy(ref SystemState state)
        {
            DisposeBlueprint();
        }

        /// <summary>
        /// When a full recipe is latched and hydrate is incomplete, build local asteroid entities
        /// in budgeted batches. Rebuilds if the join generation changed or the list was disposed.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Generation fence ---
            // [TITAN-ORBIT] Cache.Clear / a fresh ApplyRecipe bumps SessionGeneration. ISystem
            // fields live as long as ClientWorld — they must not keep a finished or disposed
            // blueprint across a second join in the same Play.
            int generation = ClientMapHydrateCache.SessionGeneration;
            if (generation != _appliedGeneration)
            {
                DisposeBlueprint();
                _blueprintReady = false;
                _loggedWaitingPrefabs = false;
                _appliedGeneration = generation;
            }

            if (ClientMapHydrateCache.IsComplete)
                return;

            if (!ClientMapHydrateCache.HasFullRecipe)
                return;

            var em = state.EntityManager;

            // --- Build blueprint (or rebuild if lists vanished) ---
            if (!_blueprintReady || !_bodies.IsCreated)
            {
                if (!TryGetAsteroidPrefab(ref state, out _))
                {
                    ClientMapHydrateCache.WaitingForPrefabs = true;
                    float now = Time.realtimeSinceStartup;
                    if (!_loggedWaitingPrefabs || now >= _nextPrefabWaitLogRealtime)
                    {
                        _loggedWaitingPrefabs = true;
                        _nextPrefabWaitLogRealtime = now + 3f;
                        Debug.Log(
                            "[ClientMapHydrate] Waiting for GamePrefabs.Asteroid on ClientWorld " +
                            "(SubScene streaming). World bar stays at 0 until prefabs exist.");
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
                ClientMapHydrateCache.WaitingForPrefabs = false;
                ClientMapHydrateCache.MarkHydrateStarted(asteroidCount);

                // Prefer the server-published period (MapSessionMetaRpc). Re-rolling from the
                // recipe can disagree by a few units; wrap + IsWrapJump then treat ordinary
                // flight as a seam jump — dedicated-only snap wall (Local Host shares one static).
                if (!ToroidalMapEcs.HasValidMapSize &&
                    ToroidalMapEcs.IsValidMapSize(_rolled.MapWidth, _rolled.MapHeight))
                {
                    ToroidalMapEcs.SetMapSize(_rolled.MapWidth, _rolled.MapHeight);
                    ToroidalMap.SetMapSize(_rolled.MapWidth, _rolled.MapHeight);
                }

                Debug.Log(
                    "[ClientMapHydrate] Blueprint ready bodies=" + _bodies.Length +
                    " asteroids=" + asteroidCount +
                    " gen=" + generation +
                    " seed=" + ClientMapHydrateCache.MatchSeed +
                    " map=" + _rolled.MapWidth.ToString("F0") + "x" + _rolled.MapHeight.ToString("F0"));
            }

            if (!_bodies.IsCreated)
                return;

            if (!TryGetAsteroidPrefab(ref state, out Entity asteroidPrefab))
            {
                ClientMapHydrateCache.WaitingForPrefabs = true;
                return;
            }

            // --- Spawn asteroid batch (skip planet kinds — those arrive as ghosts) ---
            int spawnedThisFrame = 0;
            while (_bodyIndex < _bodies.Length && spawnedThisFrame < BodiesPerFrame)
            {
                var body = _bodies[_bodyIndex];
                _bodyIndex++;
                if (body.EntityKind != 3)
                    continue;

                int slot = _asteroidsSpawned;
                ClientLocalMapBodySpawn.SpawnAsteroid(em, asteroidPrefab, body, slot);
                spawnedThisFrame++;
                _asteroidsSpawned++;
                ClientMapHydrateCache.SetBuiltBodies(_asteroidsSpawned);
            }

            if (_bodyIndex < _bodies.Length)
                return;

            ClientMapHydrateCache.MarkComplete();
            DisposeBlueprint();
            _blueprintReady = false;

            Debug.Log(
                "[ClientMapHydrate] Asteroid hydrate complete built=" + ClientMapHydrateCache.BuiltBodies +
                "/" + ClientMapHydrateCache.ExpectedBodies +
                " gen=" + generation +
                " — GoInGame may proceed.");
        }

        /// <summary>
        /// Resolves the client asteroid prefab. SubScene bake can lag a few frames after connect.
        /// </summary>
        bool TryGetAsteroidPrefab(ref SystemState state, out Entity asteroidPrefab)
        {
            asteroidPrefab = Entity.Null;
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                return false;
            if (prefabs.Asteroid == Entity.Null)
                return false;
            asteroidPrefab = prefabs.Asteroid;
            return true;
        }

        /// <summary>Frees persistent blueprint lists and resets spawn cursors.</summary>
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
