using UnityEngine;
using UnityEngine.Serialization;
using TitanOrbit.Core;
using Shapes;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Orbit-zone fill for the gem moon: same Shapes immediate-mode pattern as <see cref="HomePlanetRingsDrawer"/>
    /// (flat XZ disc under the body, radial gradient).
    /// </summary>
    [ExecuteAlways]
    public class GemMoonOrbitZoneVisual : ImmediateModeShapeDrawer
    {
        [Header("Orbit Zone Fill")]
        [Tooltip("Draw the orbit zone as a filled ring with gradient: alpha at inner edge, 0 at outer edge (same idea as HomePlanetRingsDrawer).")]
        [SerializeField] private bool drawOrbitZoneFill = true;
        [FormerlySerializedAs("zoneTint")]
        [Tooltip("Match planet orbit fill (PlanetRingsDrawer): soft blue reads more translucent than white at the same alpha.")]
        [SerializeField] private Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Tooltip("Alpha at inner edge of orbit zone (moon body radius). Same default as planet orbit zone (0.3).")]
        [Range(0f, 1f)]
        [FormerlySerializedAs("zoneInnerAlpha")]
        [SerializeField] private float orbitZoneInnerAlpha = 0.3f;
        [Tooltip("Vertical offset for the moon orbit zone in moon local Y. 0 keeps it centered at moon level.")]
        [FormerlySerializedAs("heightBelowMoon")]
        [SerializeField] private float orbitZoneHeightBelowPlanet = 0f;
        [Header("Mesh Fallback")]
        [SerializeField, Range(0f, 1f)] private float meshFallbackInnerAlpha = 0.28f;
        [SerializeField] private int meshFallbackSegments = 64;

        private PlanetGemMoon moon;
        private GameObject meshFallbackObject;
        private MeshFilter meshFallbackFilter;
        private MeshRenderer meshFallbackRenderer;
        private Material meshFallbackMaterial;
        private Texture2D meshFallbackGradient;
        private float lastOuterRadius = -1f;
        private float lastInnerRadius = -1f;

        private void Awake()
        {
            moon = GetComponentInParent<PlanetGemMoon>();
            EnsureMeshFallback();
            RefreshMeshFallback(true);
        }

        public override void OnEnable()
        {
            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            // Required on URP/HDRP so Shapes subscribes to beginCameraRendering (see ImmediateModeShapeDrawer).
            base.OnEnable();
            EnsureMeshFallback();
            RefreshMeshFallback(true);
        }

        public override void OnDisable()
        {
            base.OnDisable();
        }

        private void LateUpdate()
        {
            RefreshMeshFallback(false);
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (!drawOrbitZoneFill) return;

            if (moon == null)
                moon = GetComponentInParent<PlanetGemMoon>();
            if (moon == null) return;

            // Always show the dock/orbit zone so players can navigate to it.

            // Radii must be in moon *local* space (collider space), like HomePlanetRingsDrawer uses planet-local radii.
            // World radii × localToWorldMatrix would apply planet/parent scale twice and blow up the disc.
            float outerRadiusRuntime = GetMoonShieldRadiusLocal();
            if (outerRadiusRuntime <= 0.0001f) return;

            float innerRadiusRuntime = Mathf.Min(moon.GetMoonBodyRadiusLocal(), outerRadiusRuntime * 0.98f);
            innerRadiusRuntime = Mathf.Max(0.02f, innerRadiusRuntime);
            if (outerRadiusRuntime - innerRadiusRuntime < 0.02f)
                innerRadiusRuntime = Mathf.Max(0.02f, outerRadiusRuntime - 0.02f);

            Transform t = moon.transform;
            Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
            Matrix4x4 homeMatrix = t.localToWorldMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);

            float zoneRadius = (innerRadiusRuntime + outerRadiusRuntime) * 0.5f;
            float zoneThickness = outerRadiusRuntime - innerRadiusRuntime;
            Color innerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, orbitZoneInnerAlpha);
            Color outerColor = new Color(orbitZoneTint.r, orbitZoneTint.g, orbitZoneTint.b, 0f);

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = homeMatrix;

                Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));
            }
        }

        private void EnsureMeshFallback()
        {
            if (meshFallbackObject == null)
            {
                Transform existing = transform.Find("MoonOrbitZoneMeshFallback");
                if (existing != null) meshFallbackObject = existing.gameObject;
            }
            if (meshFallbackObject == null)
            {
                meshFallbackObject = new GameObject("MoonOrbitZoneMeshFallback");
                meshFallbackObject.transform.SetParent(transform, false);
            }

            meshFallbackObject.transform.localRotation = Quaternion.identity;
            meshFallbackObject.transform.localPosition = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
            meshFallbackObject.transform.localScale = Vector3.one;

            if (meshFallbackFilter == null) meshFallbackFilter = meshFallbackObject.GetComponent<MeshFilter>();
            if (meshFallbackFilter == null) meshFallbackFilter = meshFallbackObject.AddComponent<MeshFilter>();
            if (meshFallbackRenderer == null) meshFallbackRenderer = meshFallbackObject.GetComponent<MeshRenderer>();
            if (meshFallbackRenderer == null) meshFallbackRenderer = meshFallbackObject.AddComponent<MeshRenderer>();

            if (meshFallbackMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    meshFallbackMaterial = new Material(shader);
                    ConfigureTransparentMaterial(meshFallbackMaterial);
                    meshFallbackMaterial.renderQueue = 3000;
                    meshFallbackRenderer.sharedMaterial = meshFallbackMaterial;
                    meshFallbackRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    meshFallbackRenderer.receiveShadows = false;
                }
            }

            if (meshFallbackGradient == null)
            {
                meshFallbackGradient = new Texture2D(64, 1, TextureFormat.RGBA32, false, true);
                meshFallbackGradient.wrapMode = TextureWrapMode.Clamp;
                meshFallbackGradient.filterMode = FilterMode.Bilinear;
            }
        }

        private void RefreshMeshFallback(bool force)
        {
            EnsureMeshFallback();
            if (meshFallbackRenderer != null)
                meshFallbackRenderer.enabled = drawOrbitZoneFill;
            if (!drawOrbitZoneFill || moon == null || meshFallbackFilter == null)
                return;

            float outerRadiusRuntime = GetMoonShieldRadiusLocal();
            if (outerRadiusRuntime <= 0.0001f) return;

            float innerRadiusRuntime = Mathf.Min(moon.GetMoonBodyRadiusLocal(), outerRadiusRuntime * 0.98f);
            innerRadiusRuntime = Mathf.Max(0.02f, innerRadiusRuntime);
            if (outerRadiusRuntime - innerRadiusRuntime < 0.02f)
                innerRadiusRuntime = Mathf.Max(0.02f, outerRadiusRuntime - 0.02f);

            if (!force &&
                Mathf.Abs(outerRadiusRuntime - lastOuterRadius) < 0.001f &&
                Mathf.Abs(innerRadiusRuntime - lastInnerRadius) < 0.001f)
                return;

            lastOuterRadius = outerRadiusRuntime;
            lastInnerRadius = innerRadiusRuntime;
            meshFallbackFilter.sharedMesh = BuildRingMesh(innerRadiusRuntime, outerRadiusRuntime, Mathf.Max(24, meshFallbackSegments));
            ApplyMeshFallbackGradient();
        }

        private void ApplyMeshFallbackGradient()
        {
            if (meshFallbackMaterial == null || meshFallbackGradient == null) return;

            for (int x = 0; x < meshFallbackGradient.width; x++)
            {
                float t = x / (float)(meshFallbackGradient.width - 1);
                float a = Mathf.Lerp(meshFallbackInnerAlpha, 0f, t);
                meshFallbackGradient.SetPixel(x, 0, new Color(1f, 1f, 1f, a));
            }
            meshFallbackGradient.Apply(false, false);

            meshFallbackMaterial.SetTexture("_BaseMap", meshFallbackGradient);
            meshFallbackMaterial.SetTexture("_MainTex", meshFallbackGradient);
            meshFallbackMaterial.SetColor("_BaseColor", orbitZoneTint);
            meshFallbackMaterial.SetColor("_Color", orbitZoneTint);
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

        private static Mesh BuildRingMesh(float inner, float outer, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "MoonOrbitZoneMesh";
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

        /// <summary>
        /// Match moon zone visual radius to the shield outer edge.
        /// PlanetGemMoon exposes shield radius in world units, so convert back to moon-local units.
        /// </summary>
        private float GetMoonShieldRadiusLocal()
        {
            if (moon == null) return 0f;
            float world = moon.GetMoonShieldOuterRadiusWorld();
            float scale = Mathf.Max(0.0001f, Mathf.Abs(moon.transform.lossyScale.x));
            return world / scale;
        }
    }
}
