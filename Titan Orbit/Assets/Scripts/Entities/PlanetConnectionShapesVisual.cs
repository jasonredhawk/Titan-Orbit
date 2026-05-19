using UnityEngine;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws semi‑transparent triangle territories (vertices at each planet's gem moon) in world space.
    /// Relies on PlanetConnectionSystem for data.
    /// </summary>
    [ExecuteAlways]
    public class PlanetConnectionShapesVisual : ImmediateModeShapeDrawer
    {
        [Header("Triangles")]
        [SerializeField] private float triangleHeight = -0.6f;
        [SerializeField] private float triangleAlpha = 0.04f;
        [SerializeField] private float triangleBorderThickness = 0.15f;
        [SerializeField] private float triangleBorderAlpha = 0.22f;

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (cam == null)
                return;

            var conn = PlanetConnectionSystem.Instance;
            // Fallback: if no connection system exists yet in this scene (e.g. scene without GameManager),
            // create a local one so visuals still work.
            if (conn == null)
            {
                var go = new GameObject("PlanetConnectionSystem_Auto");
                conn = go.AddComponent<TitanOrbit.Systems.PlanetConnectionSystem>();
            }

            if (conn == null)
                return;

            var triangles = conn.CurrentTriangles;
            if (triangles == null || triangles.Count == 0)
                return;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.LineGeometry = LineGeometry.Flat2D;

                // Use unwrapped positions (one vertex + shortest-path offsets) so the triangle
                // is always the short way; when a planet blips to the other side we don't draw the long-way triangle.
                if (triangles != null)
                {
                    Vector3 camPos = cam.transform.position;
                    float mapW = ToroidalMap.GetMapWidth();
                    float mapH = ToroidalMap.GetMapHeight();
                    foreach (var tri in triangles)
                    {
                        if (tri.A == null || tri.B == null || tri.C == null)
                            continue;

                        PlanetConnectionSystem.GetStableTriangleOrder(tri, out Planet anchor, out Planet bPlanet, out Planet cPlanet);
                        Vector3 aCanon = PlanetConnectionSystem.GetTriangleVertexToroidalPosition(anchor);
                        Vector2 bOff = ToroidalMap.ShortestOffsetXZ(aCanon, PlanetConnectionSystem.GetTriangleVertexToroidalPosition(bPlanet));
                        Vector2 cOff = ToroidalMap.ShortestOffsetXZ(aCanon, PlanetConnectionSystem.GetTriangleVertexToroidalPosition(cPlanet));
                        Vector3 a = ToroidalMap.GetDisplayPosition(aCanon, camPos);
                        Vector3 b = a + new Vector3(bOff.x, 0f, bOff.y);
                        Vector3 c = a + new Vector3(cOff.x, 0f, cOff.y);
                        a.y = triangleHeight;
                        b.y = triangleHeight;
                        c.y = triangleHeight;

                        Color baseColor = TeamManager.GetTeamColor(tri.Team);
                        Color fillColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleAlpha);
                        Color borderColor = new Color(baseColor.r, baseColor.g, baseColor.b, triangleBorderAlpha);

                        DrawTriangleWithWraps(a, b, c, mapW, mapH, fillColor, borderColor);
                    }
                }
            }
        }

        /// <summary>
        /// Draws a triangle and duplicate copies at toroidal offsets so wrapping is visible on adjacent tiles.
        /// </summary>
        private void DrawTriangleWithWraps(Vector3 a, Vector3 b, Vector3 c, float mapW, float mapH,
            Color fillColor, Color borderColor)
        {
            Draw.Triangle(a, b, c, fillColor);
            Draw.TriangleBorder(a, b, c, triangleBorderThickness, borderColor);

            Vector3[] offsets = {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH)
            };
            foreach (var off in offsets)
            {
                Vector3 a2 = a + off;
                Vector3 b2 = b + off;
                Vector3 c2 = c + off;
                Draw.Triangle(a2, b2, c2, fillColor);
                Draw.TriangleBorder(a2, b2, c2, triangleBorderThickness, borderColor);
            }
        }
    }
}

