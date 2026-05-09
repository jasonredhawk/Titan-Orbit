using System.Collections.Generic;
using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a regular (non-home) planet using Shapes.
    /// Ring count = planet level (1–6), matching max regular planet level. One ring band per level.
    /// Orbit zone + rings also get mesh geometry (same approach as <see cref="GemMoonOrbitZoneVisual"/>) so visuals show even when IM Shapes are culled or fail on a given URP/camera setup.
    /// </summary>
    [ExecuteAlways]
    public class PlanetRingsDrawer : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [Tooltip("Tilt angle (degrees around X). Negative = tilted down like Saturn.")]
        [SerializeField] private float tiltDegrees = -26.7f;
        [Tooltip("Inner radius of the first ring band (planet unit radius ~0.5).")]
        [SerializeField] private float innerRadius = 0.68f;
        [Tooltip("Radial width of each ring band.")]
        [SerializeField] private float ringThickness = 0.06f;
        [Tooltip("Gap between ring bands.")]
        [SerializeField] private float gapBetweenBands = 0.015f;
        [Header("Appearance")]
        [Tooltip("Opacity of the ring.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float ringOpacity = 0.6f;

        [Header("Orbit Zone Fill")]
        [Tooltip("Draw the orbit zone as a filled ring with gradient: 0.3 alpha at inner edge, 0 at outer edge.")]
        [SerializeField] private bool drawOrbitZoneFill = true;
        [Tooltip("Inner radius of orbit zone (planet local).")]
        [SerializeField] private float orbitZoneInnerRadius = 0.5f;
        [Tooltip("Outer radius of orbit zone (planet local).")]
        [SerializeField] private float orbitZoneOuterRadius = 0.85f;
        [SerializeField] private Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Tooltip("Alpha at inner edge of orbit zone.")]
        [Range(0f, 1f)]
        [SerializeField] private float orbitZoneInnerAlpha = 0.3f;
        [Tooltip("Draw the orbit zone this far below the planet (local Y) so ships and gems render above it.")]
        [SerializeField] private float orbitZoneHeightBelowPlanet = 1f;

        [Header("Mesh backup (GemMoonOrbitZoneVisual pattern)")]
        [SerializeField, Range(0f, 1f)] private float orbitMeshInnerAlpha = 0.28f;
        [SerializeField] private int meshSegments = 96;

        private Planet planet;
        private GameObject orbitMeshObject;
        private MeshFilter orbitMeshFilter;
        private MeshRenderer orbitMeshRenderer;
        private Material orbitMeshMaterial;
        private Texture2D orbitMeshGradient;
        private float lastOrbitOuter = -1f;
        private float lastOrbitInner = -1f;

        private readonly List<GameObject> ringBandObjects = new List<GameObject>();
        private readonly List<MeshFilter> ringBandFilters = new List<MeshFilter>();
        private readonly List<MeshRenderer> ringBandRenderers = new List<MeshRenderer>();
        private int lastRingBandCount = -1;

        private void Awake()
        {
            planet = GetComponentInParent<Planet>();
            EnsureOrbitMeshChild();
            EnsureRingBandChildren();
            RefreshOrbitMesh(true);
            RefreshRingMeshes(true);
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            EnsureOrbitMeshChild();
            EnsureRingBandChildren();
            RefreshOrbitMesh(true);
            RefreshRingMeshes(true);
        }

        public override void OnDisable()
        {
            base.OnDisable();
        }

        private void LateUpdate()
        {
            RefreshOrbitMesh(false);
            RefreshRingMeshes(false);
        }

        private void OnDestroy()
        {
            if (orbitMeshMaterial != null)
                Destroy(orbitMeshMaterial);
            if (orbitMeshGradient != null)
                Destroy(orbitMeshGradient);
        }

        public Vector3 GetRingAxisWorld()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null)
                return transform.up;

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            return planet.transform.TransformDirection(tilt * Vector3.forward).normalized;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null) return;

            int level = Mathf.Clamp(planet.IsSpawned ? planet.PlanetLevel : 1, 1, 6);
            int ringCount = Mathf.Clamp(level, 1, 6);

            Transform t = planet.transform;
            float outerRadiusRuntime = planet.GetOrbitZoneOuterRadiusLocal();
            if (outerRadiusRuntime <= 0.0001f) return;

            float innerRadiusRuntime = Mathf.Min(orbitZoneInnerRadius, outerRadiusRuntime * 0.98f);
            innerRadiusRuntime = Mathf.Max(0.02f, innerRadiusRuntime);
            if (outerRadiusRuntime - innerRadiusRuntime < 0.02f)
                innerRadiusRuntime = Mathf.Max(0.02f, outerRadiusRuntime - 0.02f);

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            Matrix4x4 planetMatrix = t.localToWorldMatrix;

            if (cam != null && drawOrbitZoneFill)
            {
                Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
                Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
                Matrix4x4 zoneMatrix = planetMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
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
                    Draw.Matrix = zoneMatrix;
                    Draw.Ring(Vector3.zero, Quaternion.identity, zoneRadius, zoneThickness, DiscColors.Radial(innerColor, outerColor));
                }
            }

            if (cam != null)
            {
                Color baseColor = TeamManager.GetTeamColor(planet.TeamOwnership);
                Color color = new Color(baseColor.r, baseColor.g, baseColor.b, ringOpacity);
                using (Draw.Command(cam))
                {
                    Draw.ResetAllDrawStates();
                    Draw.RadiusSpace = ThicknessSpace.Meters;
                    Draw.ThicknessSpace = ThicknessSpace.Meters;
                    Draw.DiscGeometry = DiscGeometry.Flat2D;
                    Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                    float currentRadius = innerRadius;
                    for (int i = 0; i < ringCount; i++)
                    {
                        Draw.Ring(Vector3.zero, Quaternion.identity, currentRadius, ringThickness, color);
                        currentRadius += ringThickness + gapBetweenBands;
                    }
                }
            }
        }

        private void EnsureOrbitMeshChild()
        {
            if (orbitMeshObject == null)
            {
                Transform existing = transform.Find("PlanetOrbitZoneMeshVisual");
                if (existing != null)
                    orbitMeshObject = existing.gameObject;
            }
            if (orbitMeshObject == null)
            {
                orbitMeshObject = new GameObject("PlanetOrbitZoneMeshVisual");
                orbitMeshObject.transform.SetParent(transform, false);
            }

            orbitMeshObject.transform.localRotation = Quaternion.identity;
            orbitMeshObject.transform.localPosition = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
            orbitMeshObject.transform.localScale = Vector3.one;

            if (orbitMeshFilter == null)
                orbitMeshFilter = orbitMeshObject.GetComponent<MeshFilter>();
            if (orbitMeshFilter == null)
                orbitMeshFilter = orbitMeshObject.AddComponent<MeshFilter>();
            if (orbitMeshRenderer == null)
                orbitMeshRenderer = orbitMeshObject.GetComponent<MeshRenderer>();
            if (orbitMeshRenderer == null)
                orbitMeshRenderer = orbitMeshObject.AddComponent<MeshRenderer>();

            if (orbitMeshMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    orbitMeshMaterial = new Material(shader);
                    PlanetRingMeshBuilder.ConfigureTransparentMaterial(orbitMeshMaterial);
                    orbitMeshMaterial.renderQueue = 3000;
                    orbitMeshRenderer.sharedMaterial = orbitMeshMaterial;
                    orbitMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    orbitMeshRenderer.receiveShadows = false;
                }
            }

            if (orbitMeshGradient == null)
            {
                orbitMeshGradient = new Texture2D(64, 1, TextureFormat.RGBA32, false, true);
                orbitMeshGradient.wrapMode = TextureWrapMode.Clamp;
                orbitMeshGradient.filterMode = FilterMode.Bilinear;
            }
        }

        private void RefreshOrbitMesh(bool force)
        {
            EnsureOrbitMeshChild();
            if (orbitMeshRenderer != null)
                orbitMeshRenderer.enabled = drawOrbitZoneFill;
            if (!drawOrbitZoneFill || planet == null || orbitMeshFilter == null)
                return;

            float outer = planet.GetOrbitZoneOuterRadiusLocal();
            if (outer <= 0.0001f) return;
            float inner = Mathf.Min(orbitZoneInnerRadius, outer * 0.98f);
            inner = Mathf.Max(0.02f, inner);
            if (outer - inner < 0.02f)
                inner = Mathf.Max(0.02f, outer - 0.02f);

            if (!force && Mathf.Abs(outer - lastOrbitOuter) < 0.001f && Mathf.Abs(inner - lastOrbitInner) < 0.001f)
                return;

            lastOrbitOuter = outer;
            lastOrbitInner = inner;
            orbitMeshFilter.sharedMesh = PlanetRingMeshBuilder.BuildRingMesh(inner, outer, Mathf.Max(24, meshSegments));
            ApplyOrbitMeshGradient();
        }

        private void ApplyOrbitMeshGradient()
        {
            if (orbitMeshMaterial == null || orbitMeshGradient == null) return;
            for (int x = 0; x < orbitMeshGradient.width; x++)
            {
                float t = x / (float)(orbitMeshGradient.width - 1);
                float a = Mathf.Lerp(orbitMeshInnerAlpha, 0f, t);
                orbitMeshGradient.SetPixel(x, 0, new Color(1f, 1f, 1f, a));
            }
            orbitMeshGradient.Apply(false, false);
            orbitMeshMaterial.SetTexture("_BaseMap", orbitMeshGradient);
            orbitMeshMaterial.SetTexture("_MainTex", orbitMeshGradient);
            orbitMeshMaterial.SetColor("_BaseColor", orbitZoneTint);
            orbitMeshMaterial.SetColor("_Color", orbitZoneTint);
        }

        private void EnsureRingBandChildren()
        {
            for (int i = ringBandObjects.Count; i < 6; i++)
            {
                GameObject go = new GameObject($"PlanetRingBandMesh_{i + 1}");
                go.transform.SetParent(transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                ringBandObjects.Add(go);
                ringBandFilters.Add(mf);
                ringBandRenderers.Add(mr);
            }
            for (int i = 0; i < ringBandObjects.Count; i++)
                ringBandObjects[i].SetActive(i < 6);
        }

        private void RefreshRingMeshes(bool force)
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            if (planet == null) return;

            int ringCount = Mathf.Clamp(planet.IsSpawned ? planet.PlanetLevel : 1, 1, 6);
            if (force || ringCount != lastRingBandCount)
            {
                EnsureRingBandChildren();
                Quaternion tilt = Quaternion.Euler(tiltDegrees + 90f, 0f, 0f);
                float currentRadius = innerRadius;
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");

                for (int i = 0; i < ringCount; i++)
                {
                    GameObject band = ringBandObjects[i];
                    band.SetActive(true);
                    band.transform.localPosition = Vector3.zero;
                    band.transform.localRotation = tilt;
                    band.transform.localScale = Vector3.one;

                    float ringInner = Mathf.Max(0.02f, currentRadius - ringThickness * 0.5f);
                    float ringOuter = currentRadius + ringThickness * 0.5f;
                    ringBandFilters[i].sharedMesh = PlanetRingMeshBuilder.BuildRingMesh(ringInner, ringOuter, Mathf.Max(24, meshSegments));

                    var mr = ringBandRenderers[i];
                    if (mr.sharedMaterial == null && shader != null)
                    {
                        var mat = new Material(shader);
                        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                        if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
                        mat.renderQueue = -1;
                        mr.sharedMaterial = mat;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        mr.receiveShadows = false;
                    }
                    currentRadius += ringThickness + gapBetweenBands;
                }
                for (int i = ringCount; i < 6; i++)
                    ringBandObjects[i].SetActive(false);
                lastRingBandCount = ringCount;
            }

            Color baseColor = TeamManager.GetTeamColor(planet.TeamOwnership);
            Color ringColor = new Color(baseColor.r, baseColor.g, baseColor.b, ringOpacity);
            for (int i = 0; i < ringCount; i++)
            {
                var mr = ringBandRenderers[i];
                if (mr != null && mr.sharedMaterial != null)
                {
                    mr.sharedMaterial.SetColor("_BaseColor", ringColor);
                    mr.sharedMaterial.SetColor("_Color", ringColor);
                    mr.enabled = true;
                }
            }
        }
    }

    /// <summary>Shared ring/orbit mesh geometry (same mesh layout as <see cref="GemMoonOrbitZoneVisual"/>).</summary>
    internal static class PlanetRingMeshBuilder
    {
        internal static void ConfigureTransparentMaterial(Material mat)
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

        internal static Mesh BuildRingMesh(float inner, float outer, int segs)
        {
            Mesh mesh = new Mesh();
            mesh.name = "PlanetRingOrbitMesh";
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
