using System.Collections.Generic;
using UnityEngine;
using Shapes;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws Saturn-style tilted rings around a HomePlanet using Shapes immediate mode.
    /// Ring count = Home Planet level (1–6). Level 1 has 1 band, adds one per level up to 6.
    /// Each band is drawn as a few varied sub-rings plus dense granule discs.
    /// Optional MeshRenderer backup matches <see cref="GemMoonOrbitZoneVisual"/> / <see cref="PlanetRingsDrawer"/> when Shapes IM fails. Keep off when IM works to avoid duplicate transparent geometry flickering against tilted rings.
    /// </summary>
    [ExecuteAlways]
    public class HomePlanetRingsDrawer : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [Tooltip("Tilt angle (degrees around X). Negative = tilted down so rings pass in front of and behind the planet.")]
        [SerializeField] private float tiltDegrees = -26.7f;
        [Tooltip("Inner radius of the first ring band (planet unit radius ~0.5). Larger = more space from the planet.")]
        [SerializeField] private float innerRadius = 0.68f;
        [Tooltip("Radial width of each ring band.")]
        [SerializeField] private float ringThickness = 0.06f;
        [Tooltip("Gap between ring bands.")]
        [SerializeField] private float gapBetweenBands = 0.015f;
        [Header("Appearance")]
        [Tooltip("Base opacity of rings. Slightly transparent so planet shows through.")]
        [Range(0.2f, 1f)]
        [SerializeField] private float ringOpacity = 0.7f;
        [Tooltip("Extra glow/brightness per level (adds to opacity).")]
        [SerializeField] private float opacityPerLevel = 0.05f;

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

        private HomePlanet homePlanet;
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
            homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                homePlanet = GetComponentInParent<Planet>() as HomePlanet;
            EnsureOrbitMeshChild();
            EnsureRingBandChildren();
            RefreshOrbitMesh(true);
            RefreshRingMeshes(true);
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                homePlanet = GetComponentInParent<Planet>() as HomePlanet;
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
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                return transform.up;

            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f);
            return homePlanet.transform.TransformDirection(tilt * Vector3.forward).normalized;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                homePlanet = GetComponentInParent<Planet>() as HomePlanet;
            if (homePlanet == null) return;

            int level = Mathf.Clamp(homePlanet.IsSpawned ? homePlanet.HomePlanetLevel : 1, 1, 6);
            int ringCount = Mathf.Clamp(level, 1, 6);

            Transform t = homePlanet.transform;
            float innerRadiusRuntime = homePlanet.GetOrbitRingInnerRadiusLocal();
            float outerRadiusRuntime = homePlanet.GetOrbitRingOuterRadiusLocal();
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
                float alpha = Mathf.Clamp01(ringOpacity + (level - 1) * opacityPerLevel);
                Color baseColor = TeamManager.GetTeamColor(homePlanet.TeamOwnership);
                using (Draw.Command(cam))
                {
                    Draw.ResetAllDrawStates();
                    Draw.RadiusSpace = ThicknessSpace.Meters;
                    Draw.ThicknessSpace = ThicknessSpace.Meters;
                    Draw.DiscGeometry = DiscGeometry.Flat2D;
                    Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                    PlanetRingMeshBuilder.DrawSaturnStyleLevelBands(
                        innerRadius, ringThickness, gapBetweenBands, ringCount,
                        baseColor, alpha, homePlanet.GetInstanceID());
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
            if (!drawOrbitZoneFill || homePlanet == null || orbitMeshFilter == null)
                return;

            float inner = homePlanet.GetOrbitRingInnerRadiusLocal();
            float outer = homePlanet.GetOrbitRingOuterRadiusLocal();
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
                GameObject go = new GameObject($"HomePlanetRingBandMesh_{i + 1}");
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
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            if (homePlanet == null)
                homePlanet = GetComponentInParent<Planet>() as HomePlanet;
            if (homePlanet == null) return;

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

            int ringCount = Mathf.Clamp(homePlanet.IsSpawned ? homePlanet.HomePlanetLevel : 1, 1, 6);
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

            int level = Mathf.Clamp(homePlanet.IsSpawned ? homePlanet.HomePlanetLevel : 1, 1, 6);
            float alpha = Mathf.Clamp01(ringOpacity + (level - 1) * opacityPerLevel);
            Color baseColor = TeamManager.GetTeamColor(homePlanet.TeamOwnership);
            Color ringColor = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
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
}
