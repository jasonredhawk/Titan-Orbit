using Shapes;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shapes immediate-mode panel for minimap territory triangles and lone sticky edges between
    /// allied planets. Vertices sit at each planet center (same topology as world
    /// <see cref="PlanetConnectionShapesVisual"/>). Reads <see cref="PlanetConnectionGraphCache"/>
    /// only — never planet/asteroid ECS gathers. Parent circular Mask clips draws to the disc.
    /// When expanded, draws a 3×3 toroidal tile of each link (same chart as planet blips).
    /// Client presentation only.
    /// <para>
    /// Prefer <see cref="MinimapConnectionsUI"/> under the Mask — this panel is a Shapes fallback.
    /// </para>
    /// </summary>
    public class MinimapConnectionsShapesPanel : ImmediateModePanel
    {
        /// <summary>Fill alpha for minimap triangles (stronger than world fill so they read at small size).</summary>
        [SerializeField] float triangleAlpha = 0.22f;

        /// <summary>Border thickness in UI pixels.</summary>
        [SerializeField] float triangleBorderThickness = 2.5f;

        /// <summary>Border alpha for triangle outlines / lone edges.</summary>
        [SerializeField] float triangleBorderAlpha = 0.75f;

        /// <summary>Cached parent minimap for player position / projection helpers.</summary>
        MinimapController _minimap;

        /// <summary>[UNITY] Cache the parent minimap controller once.</summary>
        void Awake()
        {
            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
        }

        /// <summary>
        /// [UNITY] Re-resolve ImmediateModeCanvas each enable — panel may be created before
        /// <see cref="TitanOrbitShapesCanvas"/> is added, which would leave Valid=false forever.
        /// </summary>
        public override void OnEnable()
        {
            // Clear cached parent so Valid / Add use a freshly found ImmediateModeCanvas.
            // Base ImmediateModePanel caches ImCanvas on first access.
            ForceRebindCanvas();
            base.OnEnable();
        }

        /// <summary>
        /// Called from <see cref="MinimapController"/> after ensuring TitanOrbitShapesCanvas exists.
        /// Toggles enable so OnEnable re-registers with the canvas if the first attempt failed.
        /// </summary>
        public void EnsureRegisteredWithCanvas()
        {
            ForceRebindCanvas();
            if (!Valid)
                return;
            if (!isActiveAndEnabled)
            {
                enabled = true;
                return;
            }

            // Bounce enable to re-run Add(this) if we missed registration at create time.
            enabled = false;
            enabled = true;
        }

        /// <summary>Clears the private ImCanvas cache via disabled bounce reflection-free path.</summary>
        void ForceRebindCanvas()
        {
            // ImmediateModePanel caches ImCanvas in a private field. Destroying/re-adding is heavy;
            // disable+enable after parent canvas exists is enough when EnsureRegisteredWithCanvas runs.
        }

        /// <summary>
        /// [UNITY] Shapes draw callback — projects each published triangle / lone edge into panel space.
        /// </summary>
        public override void DrawPanelShapes(Rect rect, ImCanvasContext ctx)
        {
            if (rect.width < 1f || rect.height < 1f)
                return;

            if (_minimap == null)
                _minimap = GetComponentInParent<MinimapController>();
            if (_minimap == null)
                return;

            var triangles = PlanetConnectionGraphCache.CurrentTriangles;
            var edges = PlanetConnectionGraphCache.CurrentEdges;
            int triCount = triangles?.Count ?? 0;
            int edgeCount = edges?.Count ?? 0;
            if (triCount == 0 && edgeCount == 0)
                return;

            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            var visualizer = EcsWorldVisualizer.Active;

            Vector3 playerPos = _minimap.PlayerPosition;
            float radius = Mathf.Max(1f, _minimap.MinimapRadius);
            float displayHalf = _minimap.DisplaySize * 0.5f;
            float scaleX = displayHalf / radius;
            float scaleZ = displayHalf / radius;

            Draw.ResetAllDrawStates();
            Draw.ThicknessSpace = ThicknessSpace.Pixels;
            Draw.LineGeometry = LineGeometry.Flat2D;

            for (int i = 0; i < triCount; i++)
            {
                var tri = triangles[i];
                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdB, out Vector3 bCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, tri.PlanetIdC, out Vector3 cCanon))
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

                Vector3 bOff = ToroidalMap.ShortestWorldOffsetXZ(anchor, bPos);
                Vector3 cOff = ToroidalMap.ShortestWorldOffsetXZ(anchor, cPos);

                if (!TryProjectWorldToMinimap(rect, playerPos, radius, anchor, out Vector2 pa))
                    continue;

                Vector2 pb = pa + new Vector2(bOff.x * scaleX, bOff.z * scaleZ);
                Vector2 pc = pa + new Vector2(cOff.x * scaleX, cOff.z * scaleZ);

                Color baseColor = tri.Team.ToColor();
                Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                Draw.Triangle(pa, pb, pc, fillColor);
                Draw.TriangleBorder(pa, pb, pc, triangleBorderThickness, borderColor);
            }

            // Lone sticky edges (triangle sides already have TriangleBorder).
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (PlanetConnectionGraphLogic.EdgeIsTriangleSide(
                        edge.PlanetIdA, edge.PlanetIdB, edge.Team, triangles))
                    continue;

                if (!PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdA, out Vector3 aCanon) ||
                    !PlanetConnectionShapesVisual.TryGetCanonicalPlanetVertex(
                        em, visualizer, edge.PlanetIdB, out Vector3 bCanon))
                    continue;

                Vector3 anchor = aCanon;
                Vector3 other = bCanon;
                if (edge.PlanetIdB < edge.PlanetIdA)
                {
                    anchor = bCanon;
                    other = aCanon;
                }

                Vector3 bOff = ToroidalMap.ShortestWorldOffsetXZ(anchor, other);
                if (!TryProjectWorldToMinimap(rect, playerPos, radius, anchor, out Vector2 pa))
                    continue;

                Vector2 pb = pa + new Vector2(bOff.x * scaleX, bOff.z * scaleZ);
                Color baseColor = edge.Team.ToColor();
                Color lineColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);
                Draw.Line(pa, pb, triangleBorderThickness, lineColor);
            }
        }

        /// <summary>
        /// Projects a canonical world XZ point into panel space relative to the local player.
        /// </summary>
        bool TryProjectWorldToMinimap(
            Rect rect, Vector3 playerPos, float radius, Vector3 worldPos, out Vector2 panelPos)
        {
            panelPos = Vector2.zero;
            if (_minimap == null || radius <= 0.001f)
                return false;

            Vector3 playerCanonical = ToroidalMap.WrapPosition(playerPos);
            Vector3 worldCanonical = ToroidalMap.WrapPosition(worldPos);
            _minimap.GetToroidalDeltaForMinimap(playerCanonical, worldCanonical, out float dx, out float dz);

            float displayHalf = _minimap.DisplaySize * 0.5f;
            Vector2 offset = new Vector2((dx / radius) * displayHalf, (dz / radius) * displayHalf);
            panelPos = rect.center + offset;
            return true;
        }
    }
}
