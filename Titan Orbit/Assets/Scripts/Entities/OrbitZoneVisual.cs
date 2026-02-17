using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Renders a semi-transparent ring for the orbit zone around a planet.
    /// More opaque when the local player's ship is currently orbiting this planet.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class OrbitZoneVisual : MonoBehaviour
    {
        [SerializeField] private Planet planet;
        [Tooltip("Optional. If not set, a transparent material is created at runtime.")]
        [SerializeField] private Material orbitZoneMaterial;
        [SerializeField] [Range(0.05f, 0.5f)] private float alphaWhenNotOrbiting = 0.15f;
        [SerializeField] [Range(0.3f, 0.9f)] private float alphaWhenOrbiting = 0.5f;
        [SerializeField] private Color tint = new Color(1f, 0.9f, 0.35f);

        private const float InnerRadius = 0.5f;  // Planet surface (local)
        private const float OuterRadius = 0.85f; // Orbit zone outer edge (local)
        private const int Segments = 64;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Material materialInstance;

        private void Awake()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            BuildRingMesh();
            EnsureMaterial();
        }

        private void OnDestroy()
        {
            if (materialInstance != null)
                Destroy(materialInstance);
        }

        public void SetPlanet(Planet p)
        {
            planet = p;
        }

        private void BuildRingMesh()
        {
            if (meshFilter == null) return;
            Mesh mesh = new Mesh();
            mesh.name = "OrbitZoneRing";
            int vertCount = Segments * 2 + 2;
            Vector3[] verts = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] tris = new int[Segments * 6];

            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                float a = t * Mathf.PI * 2f;
                float c = Mathf.Cos(a);
                float s = Mathf.Sin(a);
                verts[i * 2] = new Vector3(InnerRadius * c, 0f, InnerRadius * s);
                verts[i * 2 + 1] = new Vector3(OuterRadius * c, 0f, OuterRadius * s);
                uvs[i * 2] = new Vector2(0f, t);
                uvs[i * 2 + 1] = new Vector2(1f, t);
            }

            for (int i = 0; i < Segments; i++)
            {
                int i0 = i * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                int ti = i * 6;
                tris[ti] = i0; tris[ti + 1] = i2; tris[ti + 2] = i1;
                tris[ti + 3] = i1; tris[ti + 4] = i2; tris[ti + 5] = i3;
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshFilter.sharedMesh = mesh;
        }

        private void EnsureMaterial()
        {
            if (meshRenderer == null) return;
            Material source = orbitZoneMaterial;
            if (source == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                    ?? Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader != null)
                {
                    source = new Material(shader);
                    source.SetFloat("_Surface", 1f); // Transparent
                    source.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    source.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    source.SetInt("_ZWrite", 0);
                    source.renderQueue = 3000;
                }
            }
            if (source != null)
            {
                materialInstance = new Material(source);
                materialInstance.SetColor("_BaseColor", new Color(tint.r, tint.g, tint.b, alphaWhenNotOrbiting));
                meshRenderer.sharedMaterial = materialInstance;
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
            }
        }

        private void Update()
        {
            if (materialInstance == null || planet == null) return;

            bool orbiting = false;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient && NetworkManager.Singleton.SpawnManager != null)
            {
                var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                if (localPlayer != null)
                {
                    var ship = localPlayer.GetComponent<Starship>();
                    if (ship != null && ship.IsInOrbit && ship.CurrentOrbitPlanet == planet)
                        orbiting = true;
                }
            }

            float alpha = orbiting ? alphaWhenOrbiting : alphaWhenNotOrbiting;
            Color c = materialInstance.GetColor("_BaseColor");
            if (Mathf.Abs(c.a - alpha) > 0.001f)
            {
                c.a = alpha;
                c.r = tint.r;
                c.g = tint.g;
                c.b = tint.b;
                materialInstance.SetColor("_BaseColor", c);
            }
        }
    }
}
