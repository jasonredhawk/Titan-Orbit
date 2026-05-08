using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Mesh fallback for orbit-zone circle when Shapes immediate mode fails.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class OrbitZoneMeshVisualFallback : MonoBehaviour
    {
        [SerializeField] private float innerRadius = 0.5f;
        [SerializeField] private float heightBelowPlanet = 0.08f;
        [SerializeField] private Color tint = new Color(0.55f, 0.75f, 0.98f, 1f);
        [SerializeField, Range(0f, 1f)] private float innerAlpha = 0.28f;
        [SerializeField] private int segments = 96;

        private Planet planet;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Material runtimeMaterial;
        private Texture2D runtimeGradientTexture;
        private float lastOuterRadius = -1f;

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
            if (runtimeGradientTexture != null)
                Destroy(runtimeGradientTexture);
        }

        private void LateUpdate()
        {
            RefreshMesh(false);
        }

        private void Initialize()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();

            if (meshFilter == null)
                meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();

            // Mesh vertices are authored in XZ already, so keep identity to face upward.
            transform.localRotation = Quaternion.identity;
            transform.localPosition = new Vector3(0f, -heightBelowPlanet, 0f);

            if (runtimeMaterial == null && meshRenderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    runtimeMaterial = new Material(shader);
                    ConfigureTransparentMaterial(runtimeMaterial);
                    runtimeMaterial.renderQueue = 3000;
                    meshRenderer.sharedMaterial = runtimeMaterial;
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    meshRenderer.receiveShadows = false;
                    EnsureGradientTexture();
                    ApplyTintAndGradient();
                }
            }
        }

        private void RefreshMesh(bool force)
        {
            if (planet == null || meshFilter == null)
                return;

            float outerRadius = planet.GetOrbitZoneOuterRadiusLocal();
            if (!force && Mathf.Abs(outerRadius - lastOuterRadius) < 0.001f)
                return;

            lastOuterRadius = outerRadius;
            meshFilter.sharedMesh = BuildRingMesh(Mathf.Max(0.02f, innerRadius), Mathf.Max(innerRadius + 0.01f, outerRadius), Mathf.Max(24, segments));
        }

        private static Mesh BuildRingMesh(float inner, float outer, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "OrbitZoneMeshFallback";
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

        private void EnsureGradientTexture()
        {
            if (runtimeGradientTexture != null) return;
            runtimeGradientTexture = new Texture2D(64, 1, TextureFormat.RGBA32, false, true);
            runtimeGradientTexture.wrapMode = TextureWrapMode.Clamp;
            runtimeGradientTexture.filterMode = FilterMode.Bilinear;
        }

        private void ApplyTintAndGradient()
        {
            if (runtimeMaterial == null) return;
            EnsureGradientTexture();
            if (runtimeGradientTexture != null)
            {
                for (int x = 0; x < runtimeGradientTexture.width; x++)
                {
                    float t = x / (float)(runtimeGradientTexture.width - 1);
                    float a = Mathf.Lerp(innerAlpha, 0f, t);
                    runtimeGradientTexture.SetPixel(x, 0, new Color(1f, 1f, 1f, a));
                }
                runtimeGradientTexture.Apply(false, false);
                runtimeMaterial.SetTexture("_BaseMap", runtimeGradientTexture);
                runtimeMaterial.SetTexture("_MainTex", runtimeGradientTexture);
            }
            runtimeMaterial.SetColor("_BaseColor", tint);
            runtimeMaterial.SetColor("_Color", tint);
        }

        private static void ConfigureTransparentMaterial(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
    }
}
