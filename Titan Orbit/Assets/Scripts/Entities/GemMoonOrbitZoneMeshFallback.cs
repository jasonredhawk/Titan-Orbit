using UnityEngine;
using UnityEngine.Serialization;

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
        [SerializeField] private Color tint = new Color(0.55f, 0.75f, 0.98f, 1f);
        [FormerlySerializedAs("innerAlpha")]
        [SerializeField, Range(0f, 1f)] private float centerAlpha = 0.28f;
        [SerializeField] private int segments = 64;

        private PlanetGemMoon moon;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Material runtimeMaterial;
        private Texture2D runtimeGradientTexture;
        private float lastOuter = -1f;

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
            if (moon == null || meshFilter == null)
                return;

            float outer = GetOrbitZoneOuterRadiusLocal();
            if (!force && Mathf.Abs(outer - lastOuter) < 0.001f)
                return;

            lastOuter = outer;
            meshFilter.sharedMesh = BuildDiscMesh(outer, Mathf.Max(24, segments));
        }

        private float GetOrbitZoneOuterRadiusLocal()
        {
            if (moon == null) return 0f;
            float world = moon.GetMoonShieldOuterRadiusWorld();
            float scale = Mathf.Max(0.0001f, Mathf.Abs(moon.transform.lossyScale.x));
            return world / scale;
        }

        private static Mesh BuildDiscMesh(float radius, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "MoonOrbitZoneMeshFallback";
            int rimVerts = segs + 1;
            Vector3[] vertices = new Vector3[rimVerts + 1];
            Vector2[] uv = new Vector2[rimVerts + 1];
            int[] triangles = new int[segs * 3];

            vertices[0] = Vector3.zero;
            uv[0] = new Vector2(0f, 0.5f);

            for (int i = 0; i < rimVerts; i++)
            {
                float t = i / (float)segs;
                float a = t * Mathf.PI * 2f;
                int vi = i + 1;
                vertices[vi] = new Vector3(radius * Mathf.Cos(a), 0f, radius * Mathf.Sin(a));
                uv[vi] = new Vector2(1f, t);
            }

            for (int i = 0; i < segs; i++)
            {
                int ti = i * 3;
                triangles[ti] = 0;
                triangles[ti + 1] = i + 1;
                triangles[ti + 2] = i + 2;
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
                    float a = Mathf.Lerp(centerAlpha, 0f, t);
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
