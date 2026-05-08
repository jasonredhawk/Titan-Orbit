using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Mesh fallback for moon orbit-zone circle when Shapes immediate mode fails.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class GemMoonOrbitZoneMeshFallback : MonoBehaviour
    {
        [SerializeField] private float heightBelowMoon = 0.05f;
        [SerializeField] private Color tint = new Color(0.55f, 0.75f, 0.98f, 0.22f);
        [SerializeField] private int segments = 64;

        private PlanetGemMoon moon;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Material runtimeMaterial;
        private float lastOuter = -1f;
        private float lastInner = -1f;

        private void Awake()
        {
            Initialize();
            RefreshMesh(true);
        }

        private void OnEnable()
        {
            Initialize();
            RefreshMesh(true);
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
        }

        private void LateUpdate()
        {
            RefreshMesh(false);
        }

        private void Initialize()
        {
            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            if (meshFilter == null)
                meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();

            // Mesh vertices are authored in XZ already, so keep identity to face upward.
            transform.localRotation = Quaternion.identity;
            transform.localPosition = new Vector3(0f, -heightBelowMoon, 0f);

            if (runtimeMaterial == null && meshRenderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    runtimeMaterial = new Material(shader);
                    runtimeMaterial.SetColor("_BaseColor", tint);
                    runtimeMaterial.SetColor("_Color", tint);
                    runtimeMaterial.renderQueue = 3000;
                    meshRenderer.sharedMaterial = runtimeMaterial;
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    meshRenderer.receiveShadows = false;
                }
            }
        }

        private void RefreshMesh(bool force)
        {
            if (moon == null || meshFilter == null)
                return;

            float outer = moon.GetMoonDockSnapRadiusLocal();
            float inner = Mathf.Clamp(moon.GetMoonBodyRadiusLocal(), 0.02f, Mathf.Max(0.03f, outer - 0.01f));
            if (!force && Mathf.Abs(outer - lastOuter) < 0.001f && Mathf.Abs(inner - lastInner) < 0.001f)
                return;

            lastOuter = outer;
            lastInner = inner;
            meshFilter.sharedMesh = BuildRingMesh(inner, outer, Mathf.Max(24, segments));
        }

        private static Mesh BuildRingMesh(float inner, float outer, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "MoonOrbitZoneMeshFallback";
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
