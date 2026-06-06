using System.Collections.Generic;
using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a regular (non-home) planet using Shapes.
    /// Ring count = planet level (1–6), matching max regular planet level. One level band per level,
    /// each band rendered as a few varied sub-rings plus dense granule discs.
    /// Optional MeshRenderer backup matches <see cref="GemMoonOrbitZoneVisual"/> when Shapes IM is culled or fails on a given URP/camera setup. Keep it off when IM works: drawing the same transparent orbit fill and rings twice causes flicker where tilted rings intersect the flat zone.
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

        [Header("Orbit Ring Fill")]
        [Tooltip("Draw the people-transfer orbit ring (thin band, fades in/out at inner and outer edges).")]
        [SerializeField] private bool drawOrbitZoneFill = true;
        [SerializeField] private Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Tooltip("Peak alpha at the center of the orbit ring band.")]
        [Range(0f, 1f)]
        [SerializeField] private float orbitZonePeakAlpha = 0.3f;
        [Tooltip("Draw the orbit zone this far below the planet (local Y) so ships and gems render above it.")]
        [SerializeField] private float orbitZoneHeightBelowPlanet = 1f;

        [Header("Mesh backup (GemMoonOrbitZoneVisual pattern)")]
        [Tooltip("When enabled, orbit zone + ring bands are also drawn with MeshRenderers. Turn off when Shapes immediate mode works: duplicate transparent geometry z-fights with tilted rings.")]
        [SerializeField] private bool renderMeshBackupGeometry = false;
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

#if UNITY_EDITOR
        private void OnValidate()
        {
            RefreshOrbitMesh(true);
            RefreshRingMeshes(true);
        }
#endif

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
            float innerRadiusRuntime = planet.GetOrbitRingInnerRadiusLocal();
            float outerRadiusRuntime = planet.GetOrbitRingOuterRadiusLocal();
            if (outerRadiusRuntime - innerRadiusRuntime < 0.02f) return;

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            Matrix4x4 planetMatrix = t.localToWorldMatrix;

            if (cam != null && drawOrbitZoneFill)
            {
                Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
                Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
                Matrix4x4 zoneMatrix = planetMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
                PlanetRingMeshBuilder.DrawShapesOrbitRing(cam, zoneMatrix, innerRadiusRuntime, outerRadiusRuntime, orbitZoneTint, orbitZonePeakAlpha);
            }

            if (cam != null)
            {
                Color baseColor = TeamManager.GetTeamColor(planet.TeamOwnership);
                using (Draw.Command(cam))
                {
                    Draw.ResetAllDrawStates();
                    Draw.RadiusSpace = ThicknessSpace.Meters;
                    Draw.ThicknessSpace = ThicknessSpace.Meters;
                    Draw.DiscGeometry = DiscGeometry.Flat2D;
                    Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                    PlanetRingMeshBuilder.DrawSaturnStyleLevelBands(
                        innerRadius, ringThickness, gapBetweenBands, ringCount,
                        baseColor, ringOpacity, planet.GetInstanceID());
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
            {
                if (!renderMeshBackupGeometry)
                {
                    orbitMeshRenderer.enabled = false;
                    return;
                }
                orbitMeshRenderer.enabled = drawOrbitZoneFill;
            }
            if (!drawOrbitZoneFill || planet == null || orbitMeshFilter == null)
                return;

            float inner = planet.GetOrbitRingInnerRadiusLocal();
            float outer = planet.GetOrbitRingOuterRadiusLocal();
            if (outer - inner < 0.02f) return;

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
            PlanetRingMeshBuilder.FillOrbitRingGradientTexture(orbitMeshGradient, orbitZonePeakAlpha);
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

            if (!renderMeshBackupGeometry)
            {
                EnsureRingBandChildren();
                for (int i = 0; i < ringBandRenderers.Count; i++)
                {
                    if (ringBandRenderers[i] != null)
                        ringBandRenderers[i].enabled = false;
                }
                return;
            }

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

    /// <summary>Shared ring/orbit mesh geometry and planet orbit-ring drawing helpers.</summary>
    internal static class PlanetRingMeshBuilder
    {
        internal const int OrbitRingShapeGradientSteps = 32;
        private const int MinSubRingsPerLevelBand = 2;
        private const int MaxSubRingsPerLevelBand = 5;
        private const int GranulesPerLevelBand = 185;

        /// <summary>
        /// Draws one Saturn-style level band: edge frame + soft fill, interior sub-rings, and dense granules.
        /// Call inside an active Shapes Draw.Command with matrix and draw states already set.
        /// </summary>
        internal static void DrawSaturnStyleLevelBands(
            float innerRadius, float bandThickness, float bandGap, int bandCount,
            Color baseColor, float baseAlpha, int visualSeed)
        {
            float currentCenter = innerRadius;
            for (int band = 0; band < bandCount; band++)
            {
                DrawSaturnStyleLevelBand(baseColor, baseAlpha, currentCenter, bandThickness, band, visualSeed);
                currentCenter += bandThickness + bandGap;
            }
        }

        private static void DrawSaturnStyleLevelBand(
            Color baseColor, float baseAlpha, float centerRadius, float bandThickness, int bandIndex, int visualSeed)
        {
            int seed = visualSeed * 997 + bandIndex * 131;
            float bandInner = centerRadius - bandThickness * 0.5f;
            float bandOuter = centerRadius + bandThickness * 0.5f;
            float radialSpan = bandOuter - bandInner;
            if (radialSpan < 0.001f) return;

            // Level band frame — clearly marks where this level ring begins and ends.
            float edgeThickness = Mathf.Max(0.0016f, bandThickness * 0.1f);
            Color edgeColor = WithAlpha(baseColor, baseAlpha * 0.72f);
            Draw.Ring(Vector3.zero, Quaternion.identity, bandInner + edgeThickness * 0.5f, edgeThickness, edgeColor);
            Draw.Ring(Vector3.zero, Quaternion.identity, bandOuter - edgeThickness * 0.5f, edgeThickness, edgeColor);

            Color bandFillInner = WithAlpha(baseColor, baseAlpha * 0.14f);
            Color bandFillOuter = WithAlpha(baseColor, baseAlpha * 0.22f);
            Draw.Ring(Vector3.zero, Quaternion.identity, centerRadius, bandThickness * 0.88f,
                DiscColors.Radial(bandFillInner, bandFillOuter));

            int lineCount = MinSubRingsPerLevelBand +
                (int)(RingHash(seed, 1) * (MaxSubRingsPerLevelBand - MinSubRingsPerLevelBand + 0.999f));

            const float thinMin = 0.0008f;
            float thinMax = bandThickness * 0.12f;
            float inset = edgeThickness * 1.2f;
            float detailInner = bandInner + inset;
            float detailOuter = bandOuter - inset;
            float detailSpan = detailOuter - detailInner;
            if (detailSpan < 0.001f)
            {
                detailInner = bandInner;
                detailOuter = bandOuter;
                detailSpan = radialSpan;
            }

            for (int s = 0; s < lineCount; s++)
            {
                float radialPos = detailInner + RingHash(seed, s + 10) * detailSpan;

                float thinRoll = RingHash(seed, s + 82);
                float thinThickness = Mathf.Lerp(thinMin, thinMax, thinRoll);

                float widthRoll = RingHash(seed, s + 80);
                bool isWide = widthRoll > 0.45f;
                float subThickness = isWide
                    ? thinThickness * Mathf.Lerp(1.15f, 3f, RingHash(seed, s + 81))
                    : thinThickness;

                float alphaRoll = RingHash(seed, s + 120);
                float subAlpha = isWide
                    ? baseAlpha * Mathf.Lerp(0.22f, 0.48f, alphaRoll)
                    : baseAlpha * Mathf.Lerp(0.32f, 0.68f, alphaRoll);

                float brightRoll = RingHash(seed, s + 160);
                float brightness = Mathf.Lerp(0.78f, 1.18f, brightRoll);
                Color tinted = ScaleRgb(baseColor, brightness);

                if (isWide)
                {
                    Color edge = WithAlpha(tinted, subAlpha * 0.25f);
                    Color core = WithAlpha(tinted, subAlpha * 0.75f);
                    Draw.Ring(Vector3.zero, Quaternion.identity, radialPos, subThickness, DiscColors.Radial(core, edge));
                }
                else
                {
                    Color streakBright = WithAlpha(ScaleRgb(tinted, 1.05f), subAlpha);
                    Color streakDim = WithAlpha(ScaleRgb(tinted, 0.72f), subAlpha * Mathf.Lerp(0.45f, 0.85f, RingHash(seed, s + 200)));
                    float angularOffset = RingHash(seed, s + 280) * 360f;
                    Draw.Ring(Vector3.zero, Quaternion.Euler(0f, 0f, angularOffset), radialPos, subThickness,
                        DiscColors.Angular(streakBright, streakDim));
                }
            }

            for (int g = 0; g < GranulesPerLevelBand; g++)
            {
                float angle = RingHash(seed, g + 360) * Mathf.PI * 2f;
                float radialPos = detailInner + RingHash(seed, g + 400) * detailSpan;
                float sizeRoll = RingHash(seed, g + 440);
                float granuleRadius = sizeRoll > 0.9f
                    ? Mathf.Lerp(0.004f, 0.0075f, RingHash(seed, g + 441))
                    : Mathf.Lerp(0.0005f, 0.003f, sizeRoll);
                float granuleAlpha = baseAlpha * Mathf.Lerp(0.25f, 0.88f, RingHash(seed, g + 480));
                float granuleBright = Mathf.Lerp(0.82f, 1.38f, RingHash(seed, g + 520));

                Vector3 pos = new Vector3(Mathf.Cos(angle) * radialPos, Mathf.Sin(angle) * radialPos, 0f);
                Color granuleColor = WithAlpha(ScaleRgb(baseColor, granuleBright), granuleAlpha);
                Draw.Disc(pos, granuleRadius, granuleColor);

                if (RingHash(seed, g + 560) > 0.92f)
                {
                    Draw.BlendMode = ShapesBlendMode.Additive;
                    Draw.Disc(pos, granuleRadius * 0.5f, WithAlpha(ScaleRgb(baseColor, 1.4f), granuleAlpha * 0.28f));
                    Draw.BlendMode = ShapesBlendMode.Transparent;
                }
            }
        }

        private static float RingHash(int seed, int index)
        {
            float n = seed * 0.1031f + index * 0.0973f;
            return Mathf.Repeat(Mathf.Sin(n) * 43758.5453123f, 1f);
        }

        private static Color WithAlpha(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

        private static Color ScaleRgb(Color c, float scale) =>
            new Color(Mathf.Clamp01(c.r * scale), Mathf.Clamp01(c.g * scale), Mathf.Clamp01(c.b * scale), c.a);

        internal static void FillOrbitRingGradientTexture(Texture2D texture, float peakAlpha)
        {
            if (texture == null) return;
            int w = Mathf.Max(64, texture.width);
            if (texture.width != w)
            {
                texture.Reinitialize(w, 1);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
            }
            for (int x = 0; x < w; x++)
            {
                float t = w > 1 ? x / (float)(w - 1) : 0f;
                float a = peakAlpha * Mathf.Sin(t * Mathf.PI);
                texture.SetPixel(x, 0, new Color(1f, 1f, 1f, a));
            }
            texture.Apply(false, false);
        }

        /// <summary>Shapes immediate-mode orbit ring with fade-in and fade-out across the band thickness.</summary>
        internal static void DrawShapesOrbitRing(UnityEngine.Camera cam, Matrix4x4 matrix, float inner, float outer, Color tint, float peakAlpha)
        {
            float band = outer - inner;
            if (band < 0.001f) return;
            float step = band / OrbitRingShapeGradientSteps;
            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = matrix;
                for (int i = 0; i < OrbitRingShapeGradientSteps; i++)
                {
                    float r0 = inner + i * step;
                    float r1 = r0 + step;
                    float mid = (r0 + r1) * 0.5f;
                    float t = (mid - inner) / band;
                    float a = peakAlpha * Mathf.Sin(t * Mathf.PI);
                    float center = (r0 + r1) * 0.5f;
                    Draw.Ring(Vector3.zero, Quaternion.identity, center, step, new Color(tint.r, tint.g, tint.b, a));
                }
            }
        }

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
