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
    /// Client presentation cache of territory triangles whose moon vertices match
    /// <see cref="PlanetConnectionShapesVisual"/> (live moon proxies + same clock).
    /// <para>
    /// [TITAN-ORBIT] Asteroid tint must use <b>this</b> cache — not ghosted server
    /// <c>TerritoryTeam</c> / <c>TerritoryTeamsMask</c>. Server ownership runs on a different
    /// graph side, coarser moon clock (~1 Hz, no tick fraction), and ECS moon math without
    /// display proxies. That divergence made rocks the wrong colour relative to the fill
    /// the player sees (localhost and dedicated). Server ghosts stay authoritative for
    /// mining / destroy yellow gems only.
    /// </para>
    /// Safe under Windows join quarantine: never gathers asteroids; only reads Client
    /// topology + known moon/planet presentation data.
    /// </summary>
    public static class PlanetConnectionPresentationTriangles
    {
        /// <summary>Match <see cref="PlanetConnectionShapesVisual"/> moon cache rate.</summary>
        const float RebuildIntervalSeconds = 1f / 30f;

        static readonly List<PlanetConnectionGraphLogic.RuntimeTriangle> s_Managed =
            new List<PlanetConnectionGraphLogic.RuntimeTriangle>(32);

        static NativeArray<PlanetConnectionGraphLogic.RuntimeTriangle> s_Native;
        static int s_LastGraphRevision = -1;
        static float s_NextRebuildUnscaled = -999f;

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
            // --- Map size for toroidal PIT ---
            float mapW = ToroidalMap.GetMapWidth();
            float mapH = ToroidalMap.GetMapHeight();
            if (mapW < 1f) mapW = ToroidalMapEcs.MapWidth;
            if (mapH < 1f) mapH = ToroidalMapEcs.MapHeight;

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
            s_NextRebuildUnscaled = -999f;
        }

        /// <summary>Rebuilds when Client graph revision changes or the 30 Hz timer elapses.</summary>
        static void EnsureFresh()
        {
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            if (revision == s_LastGraphRevision &&
                Time.unscaledTime < s_NextRebuildUnscaled &&
                s_Native.IsCreated)
                return;

            Rebuild();
            s_LastGraphRevision = revision;
            s_NextRebuildUnscaled = Time.unscaledTime + RebuildIntervalSeconds;
        }

        /// <summary>
        /// Rebuilds moon-vertex runtime triangles from Client topology using the same
        /// <see cref="PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex"/> path as the drawer.
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

            // [TITAN-ORBIT] Same clock as PlanetConnectionShapesVisual (tick fraction ON).
            if (!PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double moonElapsed, includeTickFraction: true))
                moonElapsed = Time.timeAsDouble;

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdA, moonElapsed, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdB, moonElapsed, out Vector3 bCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalMoonVertex(
                        em, visualizer, tri.PlanetIdC, moonElapsed, out Vector3 cCanon))
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
