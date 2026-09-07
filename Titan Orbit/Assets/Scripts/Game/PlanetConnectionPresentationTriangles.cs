using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client presentation cache of territory triangles whose vertices match
    /// <see cref="PlanetConnectionShapesVisual"/> (planet centers).
    /// <para>
    /// [TITAN-ORBIT] Asteroid tint must use <b>this</b> cache — not ghosted server
    /// <c>TerritoryTeam</c> / <c>TerritoryTeamsMask</c> alone for colour match with the fill.
    /// Server ghosts stay authoritative for mining / destroy yellow gems.
    /// Planet centers (not moons) mean this cache only rebuilds when Client topology publishes.
    /// </para>
    /// Safe under Windows join quarantine: never gathers asteroids; only reads Client
    /// topology + known planet presentation data.
    /// </summary>
    public static class PlanetConnectionPresentationTriangles
    {
        static readonly List<PlanetConnectionGraphLogic.RuntimeTriangle> s_Managed =
            new List<PlanetConnectionGraphLogic.RuntimeTriangle>(32);

        static NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> s_Native;
        static int s_LastGraphRevision = -1;

        /// <summary>
        /// Ensures the presentation runtime array is fresh, then returns a Persistent
        /// <see cref="NativeArray{T}"/> (do <b>not</b> Dispose — owned by this cache).
        /// </summary>
        public static NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> GetRuntimeNative()
        {
            EnsureFresh();
            return s_Native;
        }

        /// <summary>
        /// Point-in-triangle ownership for a canonical-wrapped world position using the
        /// same verts as the drawn territory fill.
        /// </summary>
        /// <param name="canonicalWorldPos">XZ wrapped into map space (Y ignored).</param>
        /// <param name="mask">All owning team bits (overlap = multiple).</param>
        /// <param name="primaryTeam">Strongest gem-mult team, or None.</param>
        public static void GetOwnershipAtPosition(
            float3 canonicalWorldPos,
            out byte mask,
            out TeamId primaryTeam)
        {
            if (!ToroidalMap.TryGetMapSize(out float mapW, out float mapH) &&
                !ToroidalMapEcs.TryGetMapSize(out mapW, out mapH))
            {
                mask = 0;
                primaryTeam = TeamId.None;
                return;
            }

            var runtime = GetRuntimeNative();
            PlanetConnectionGraphLogic.GetTerritoryOwnershipAtPosition(
                canonicalWorldPos,
                runtime,
                mapW,
                mapH,
                out mask,
                out primaryTeam);
        }

        /// <summary>Clears topology + Persistent native (leave session / domain reload).</summary>
        public static void Clear()
        {
            s_Managed.Clear();
            DisposeNative();
            s_LastGraphRevision = -1;
        }

        /// <summary>
        /// Rebuilds when Client graph revision changes, or when the last pass resolved
        /// fewer triangles than published (planet proxies / map size were not ready yet).
        /// </summary>
        static void EnsureFresh()
        {
            // --- Same one-shot trap as the world / minimap drawers ---
            // [TITAN-ORBIT] Locking s_LastGraphRevision after an empty Rebuild() left PIT
            // and asteroid tint stale while Shapes still retried. Retry until the native
            // array has one runtime triangle per published Client triangle (or the graph is empty).
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            int published = PlanetConnectionGraphCache.CurrentTriangles?.Count ?? 0;
            bool incomplete = published > 0 && (!s_Native.IsCreated || s_Native.Length < published);
            if (revision == s_LastGraphRevision && s_Native.IsCreated && !incomplete)
                return;

            Rebuild();
            s_LastGraphRevision = revision;
        }

        /// <summary>
        /// Rebuilds planet-center runtime triangles from Client topology using the same
        /// <see cref="PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex"/> path as the drawer.
        /// </summary>
        static void Rebuild()
        {
            s_Managed.Clear();

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
            {
                SyncNativeFromManaged();
                return;
            }

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                SyncNativeFromManaged();
                return;
            }

            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
                    continue;

                s_Managed.Add(new PlanetConnectionGraphLogic.RuntimeTriangle
                {
                    VertexA = new float3(aCanon.x, 0f, aCanon.z),
                    VertexB = new float3(bCanon.x, 0f, bCanon.z),
                    VertexC = new float3(cCanon.x, 0f, cCanon.z),
                    Team = tri.Team,
                    GemBonusMultiplier = tri.GemBonusMultiplier,
                    AverageLevel = tri.AverageLevel,
                    PlanetIdA = tri.PlanetIdA,
                    PlanetIdB = tri.PlanetIdB,
                    PlanetIdC = tri.PlanetIdC,
                });
            }

            SyncNativeFromManaged();
        }

        /// <summary>Copies managed list into Persistent native for Burst-friendly PIT helpers.</summary>
        static void SyncNativeFromManaged()
        {
            DisposeNative();
            int n = s_Managed.Count;
            s_Native = new NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle>(
                n, Allocator.Persistent);
            for (int i = 0; i < n; i++)
                s_Native[i] = s_Managed[i];
        }

        /// <summary>Frees Persistent native if allocated.</summary>
        static void DisposeNative()
        {
            if (s_Native.IsCreated)
                s_Native.Dispose();
            s_Native = default;
        }

        /// <summary>
        /// [UNITY] Domain reload / process start — Persistent allocs must not leak across Play Mode.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();
    }
}
