using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Mesh fallback for planet level rings when Shapes immediate mode fails.
    /// </summary>
    [ExecuteAlways]
    public class PlanetRingsMeshVisualFallback : MonoBehaviour
    {
        [SerializeField] private float tiltDegrees = -26.7f;
        [SerializeField] private float innerRadius = 0.68f;
        [SerializeField] private float ringThickness = 0.06f;
        [SerializeField] private float gapBetweenBands = 0.015f;
        [SerializeField, Range(0.2f, 1f)] private float ringOpacity = 0.58f;
        [SerializeField] private int segments = 96;

        private Planet planet;
        private readonly List<GameObject> bands = new List<GameObject>();
        private readonly List<MeshFilter> filters = new List<MeshFilter>();
        private readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        private int lastRingCount = -1;

        private void Awake()
        {
            planet = GetComponentInParent<Planet>();
        }

        private void OnEnable()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            RefreshBands(true);
        }

        private void LateUpdate()
        {
            RefreshBands(false);
        }

        private void RefreshBands(bool forceRebuild)
        {
            if (planet == null)
                return;

            int ringCount = Mathf.Clamp(planet.PlanetLevel, 1, 6);
            if (forceRebuild || ringCount != lastRingCount)
            {
                EnsureBandCount(ringCount);
                BuildBandMeshes();
                lastRingCount = ringCount;
            }

            Color baseColor = TeamManager.GetTeamColor(planet.TeamOwnership);
            Color ringColor = new Color(baseColor.r, baseColor.g, baseColor.b, ringOpacity);
            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);

            for (int i = 0; i < bands.Count; i++)
            {
                GameObject band = bands[i];
                if (band == null) continue;
                band.transform.localPosition = Vector3.zero;
                band.transform.localRotation = tilt;
                band.transform.localScale = Vector3.one;

                var mr = renderers[i];
                if (mr != null && mr.sharedMaterial != null)
                {
                    mr.sharedMaterial.SetColor("_BaseColor", ringColor);
                    mr.sharedMaterial.SetColor("_Color", ringColor);
                    mr.enabled = true;
                }
            }
        }

        private void EnsureBandCount(int count)
        {
            for (int i = bands.Count; i < count; i++)
            {
                GameObject go = new GameObject($"RingFallback_{i + 1}");
                go.transform.SetParent(transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                bands.Add(go);
                filters.Add(mf);
                renderers.Add(mr);
            }

            for (int i = 0; i < bands.Count; i++)
            {
                if (bands[i] != null)
                    bands[i].SetActive(i < count);
            }
        }

        private void BuildBandMeshes()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            float currentRadius = innerRadius;
            for (int i = 0; i < bands.Count; i++)
            {
                if (bands[i] == null || !bands[i].activeSelf) continue;
                float ringInner = Mathf.Max(0.02f, currentRadius - ringThickness * 0.5f);
                float ringOuter = currentRadius + ringThickness * 0.5f;
                filters[i].sharedMesh = BuildRingMesh(ringInner, ringOuter, Mathf.Max(24, segments));
                if (renderers[i].sharedMaterial == null && shader != null)
                {
                    var mat = new Material(shader);
                    mat.renderQueue = 3000;
                    renderers[i].sharedMaterial = mat;
                    renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderers[i].receiveShadows = false;
                }
                currentRadius += ringThickness + gapBetweenBands;
            }
        }

        private static Mesh BuildRingMesh(float inner, float outer, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "PlanetRingsMeshFallback";
            int vertexCount = (segs + 1) * 2;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];
            int[] triangles = new int[segs * 6];

            for (int i = 0; i <= segs; i++)
            {
                float t = i / (float)segs;
                float a = t * Mathf.PI * 2f;
                float c = Mathf.Cos(a);
                float s = Mathf.Sin(a);
                int vi = i * 2;
                vertices[vi] = new Vector3(inner * c, 0f, inner * s);
                vertices[vi + 1] = new Vector3(outer * c, 0f, outer * s);
                uv[vi] = new Vector2(0f, t);
                uv[vi + 1] = new Vector2(1f, t);
            }

            for (int i = 0; i < segs; i++)
            {
                int vi = i * 2;
                int ti = i * 6;
                triangles[ti] = vi;
                triangles[ti + 1] = vi + 2;
                triangles[ti + 2] = vi + 1;
                triangles[ti + 3] = vi + 1;
                triangles[ti + 4] = vi + 2;
                triangles[ti + 5] = vi + 3;
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
