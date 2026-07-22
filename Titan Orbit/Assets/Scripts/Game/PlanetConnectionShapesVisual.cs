using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Draws semi-transparent team territory triangles in world space (vertices at each
    /// planet's gem moon). Reads topology from <see cref="PlanetConnectionGraphCache"/> — never
    /// runs planet/asteroid ECS gathers. Port of NGO <c>PlanetConnectionShapesVisual</c>.
    /// <para>
    /// [TITAN-ORBIT] Moon proxies live in <b>display</b> space (retiled near the ship). We always
    /// wrap vertices to canonical XZ, unwrap B/C via shortest-path offsets from the anchor, then
    /// retiling the whole triangle near the camera — so seams stretch across the wrap instead of
    /// placing a vertex a full map away (or looking “high” on the play plane).
    /// </para>
    /// <para>
    /// Moon world verts are cached (~30 Hz); each DrawShapes only retiles + draws. That keeps
    /// camera callbacks cheap while moons still orbit smoothly enough for territory fill.
    /// </para>
    /// Client presentation only.
    /// </summary>
    [ExecuteAlways]
    public class PlanetConnectionShapesVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Y height of the flat triangle fill (slightly below the play plane).</summary>
        [SerializeField] float triangleHeight = -0.6f;

        /// <summary>Fill alpha for territory triangles (original ~0.04).</summary>
        [SerializeField] float triangleAlpha = 0.04f;

        /// <summary>Border thickness in meters.</summary>
        [SerializeField] float triangleBorderThickness = 0.15f;

        /// <summary>Border alpha (original ~0.22).</summary>
        [SerializeField] float triangleBorderAlpha = 0.22f;

        /// <summary>How often moon canonical verts refresh (retile still runs every DrawShapes).</summary>
        const float MoonCacheIntervalSeconds = 1f / 30f;

        /// <summary>Cached canonical triangle (anchor + B/C offsets + colours).</summary>
        struct CachedWorldTriangle
        {
            public Vector3 Anchor;
            public Vector3 OffsetB;
            public Vector3 OffsetC;
            public Color Fill;
            public Color Border;
        }

        readonly List<CachedWorldTriangle> _worldCache = new List<CachedWorldTriangle>(16);
        int _lastGraphRevision = -1;
        float _nextMoonCacheTime;

        /// <summary>
        /// Ensures a drawer exists under <c>PlanetConnectionSystems</c> when the client is in-game.
        /// </summary>
        public static void EnsureExists()
        {
            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            if (go.GetComponent<PlanetConnectionShapesVisual>() == null)
                go.AddComponent<PlanetConnectionShapesVisual>();
        }

        /// <summary>
        /// [UNITY] Shapes draw callback — fills + borders for every published triangle, plus wrap copies.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (cam == null)
                return;

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
                return;

            // Refresh moon verts on topology change or ~30 Hz — not every camera callback.
            int revision = PlanetConnectionGraphCache.ClientPublishRevision;
            if (revision != _lastGraphRevision || Time.unscaledTime >= _nextMoonCacheTime)
            {
                RebuildWorldCache();
                _lastGraphRevision = revision;
                _nextMoonCacheTime = Time.unscaledTime + MoonCacheIntervalSeconds;
            }

            if (_worldCache.Count == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.LineGeometry = LineGeometry.Flat2D;

                Vector3 referencePos = ResolveDisplayReference(cam, em);
                float mapW = ToroidalMap.GetMapWidth();
                float mapH = ToroidalMap.GetMapHeight();

                for (int i = 0; i < _worldCache.Count; i++)
                {
                    var tri = _worldCache[i];
                    Vector3 a = ToroidalMap.GetDisplayPosition(tri.Anchor, referencePos);
                    Vector3 b = a + tri.OffsetB;
                    Vector3 c = a + tri.OffsetC;
                    a.y = triangleHeight;
                    b.y = triangleHeight;
                    c.y = triangleHeight;
                    DrawTriangleWithWraps(a, b, c, mapW, mapH, tri.Fill, tri.Border);
                }
            }
        }

        /// <summary>Resolves gem-moon vertices into canonical world space (expensive path).</summary>
        void RebuildWorldCache()
        {
            _worldCache.Clear();

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;

            if (!PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double moonElapsed, includeTickFraction: true))
                moonElapsed = Time.timeAsDouble;

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                if (!TryGetCanonicalMoonVertex(em, visualizer, tri.PlanetIdA, moonElapsed, out Vector3 aCanon) ||
                    !TryGetCanonicalMoonVertex(em, visualizer, tri.PlanetIdB, moonElapsed, out Vector3 bCanon) ||
                    !TryGetCanonicalMoonVertex(em, visualizer, tri.PlanetIdC, moonElapsed, out Vector3 cCanon))
                    continue;

                int idA = tri.PlanetIdA, idB = tri.PlanetIdB, idC = tri.PlanetIdC;
                Vector3 anchor = aCanon, bPos = bCanon, cPos = cCanon;
                if (idB <= idA && idB <= idC)
                {
                    anchor = bCanon;
                    bPos = aCanon;
                    cPos = cCanon;
                }
                else if (idC <= idA && idC <= idB)
                {
                    anchor = cCanon;
                    bPos = aCanon;
                    cPos = bCanon;
                }

                Color baseColor = tri.Team.ToColor();
                _worldCache.Add(new CachedWorldTriangle
                {
                    Anchor = anchor,
                    OffsetB = ToroidalMap.ShortestWorldOffsetXZ(anchor, bPos),
                    OffsetC = ToroidalMap.ShortestWorldOffsetXZ(anchor, cPos),
                    Fill = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha),
                    Border = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha),
                });
            }
        }

        /// <summary>
        /// Draws a triangle and ±map-tile copies only when a vertex sits near a wrap seam
        /// (avoids 5× Shapes draws for every triangle every camera callback).
        /// </summary>
        void DrawTriangleWithWraps(
            Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH, Color fillColor, Color borderColor)
        {
            Draw.Triangle(a, b, c, fillColor);
            Draw.TriangleBorder(a, b, c, triangleBorderThickness, borderColor);

            if (!NeedsWrapCopies(a, b, c, mapW, mapH))
                return;

            Vector3[] offsets =
            {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 off = offsets[i];
                Draw.Triangle(a + off, b + off, c + off, fillColor);
                Draw.TriangleBorder(a + off, b + off, c + off, triangleBorderThickness, borderColor);
            }
        }

        /// <summary>True when any vertex is within a margin of a map edge (seam-visible case).</summary>
        static bool NeedsWrapCopies(Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH)
        {
            float margin = math.max(20f, 0.08f * math.min(mapW, mapH));
            return NearEdge(a, mapW, mapH, margin) ||
                   NearEdge(b, mapW, mapH, margin) ||
                   NearEdge(c, mapW, mapH, margin);
        }

        /// <summary>Canonical XZ near 0 or map extent.</summary>
        static bool NearEdge(Vector3 p, float mapW, float mapH, float margin)
        {
            Vector3 w = ToroidalMap.WrapPosition(p);
            return w.x < margin || w.x > mapW - margin ||
                   w.z < margin || w.z > mapH - margin;
        }

        /// <summary>
        /// Local ship presentation pose when available — keeps triangles retiled with the rest of the map.
        /// </summary>
        static Vector3 ResolveDisplayReference(Camera cam, EntityManager em)
        {
            if (EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 shipPos))
            {
                shipPos.y = 0f;
                return shipPos;
            }

            Vector3 camPos = cam.transform.position;
            camPos.y = 0f;
            return camPos;
        }

        /// <summary>
        /// Moon world XZ wrapped into canonical toroidal space (Y forced to 0).
        /// Prefers live moon proxy, else ECS planet pose + orbit math.
        /// </summary>
        public static bool TryGetCanonicalMoonVertex(
            EntityManager em,
            EcsWorldVisualizer visualizer,
            int planetId,
            double moonElapsed,
            out Vector3 moonCanonical)
        {
            moonCanonical = default;
            if (planetId == 0)
                return false;

            Vector3 raw;
            if (PlanetGemMoonVisualRegistry.TryGetMoon(planetId, out var moonProxy) && moonProxy != null)
            {
                raw = moonProxy.MoonWorldPosition;
            }
            else if (visualizer != null &&
                     visualizer.TryGetPlanetPoseByPlanetId(
                         em, planetId, out float3 planetPos, out float scale, out PlanetState state))
            {
                float3 moon = PlanetOrbitMath.GetMoonWorldPosition(
                    planetPos,
                    math.max(0.25f, scale),
                    state.PlanetLevel,
                    state.PlanetId,
                    moonElapsed,
                    state.IsHomePlanet);
                raw = new Vector3(moon.x, 0f, moon.z);
            }
            else
            {
                return false;
            }

            raw.y = 0f;
            moonCanonical = ToroidalMap.WrapPosition(raw);
            moonCanonical.y = 0f;
            return true;
        }
    }
}
