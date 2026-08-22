using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Opaque non-colliding globe so the far hemisphere is not visible through the playable shell.
    /// Gameplay never uses this mesh for collision — one set of ECS colliders only.
    /// </summary>
    [DefaultExecutionOrder(65990)]
    public sealed class SphereMapGlobeVisual : MonoBehaviour
    {
        /// <summary>
        /// Visual globe sits just inside the playable shell. Largest planets use scale 18
        /// (Unity sphere mesh radius 9). Inset a hair past that so planet meshes are not
        /// clipped, without leaving a hole that shows the far-side territory lines.
        /// </summary>
        const float InsetWorld = 10f;
        const string GlobeName = "SphereMapGlobe";

        static SphereMapGlobeVisual s_Instance;
        MeshRenderer _renderer;
        Transform _xf;
        float _appliedVisual;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            Ensure();
        }

        /// <summary>Creates or finds the globe and matches it to the latched map radius.</summary>
        public static void Ensure()
        {
            if (s_Instance == null)
            {
                var existing = GameObject.Find(GlobeName);
                if (existing != null)
                {
                    StripGameplayColliders(existing);
                    s_Instance = existing.GetComponent<SphereMapGlobeVisual>()
                                 ?? existing.AddComponent<SphereMapGlobeVisual>();
                }
                else
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = GlobeName;
                    StripGameplayColliders(go);
                    s_Instance = go.AddComponent<SphereMapGlobeVisual>();
                }
            }

            s_Instance.SyncRadius();
        }

        void Awake()
        {
            s_Instance = this;
            _xf = transform;
            _renderer = GetComponent<MeshRenderer>();
            StripGameplayColliders(gameObject);
            ApplyMaterial();
        }

        static void StripGameplayColliders(GameObject go)
        {
            if (go == null)
                return;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (Application.isPlaying)
                    Object.Destroy(col);
                else
                    Object.DestroyImmediate(col);
            }
        }

        void LateUpdate()
        {
            SyncRadius();
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            if (_renderer != null && _renderer.sharedMaterial != null &&
                _renderer.sharedMaterial.name.StartsWith("SphereMapGlobeSpaceMat"))
                Destroy(_renderer.sharedMaterial);
        }

        void SyncRadius()
        {
            float radius = 0f;
            if (MapSessionMetaCache.MapRadius > SphericalMapEcs.MinValidRadius)
                radius = MapSessionMetaCache.MapRadius;
            else if (!SphericalMapEcs.TryGetRadius(out radius))
            {
                if (SphericalMapEcs.TryGetMapSize(out float mapSize))
                    radius = SphericalMapEcs.RadiusFromMapSize(mapSize);
            }

            if (!SphericalMapEcs.IsValidRadius(radius))
            {
                if (_renderer != null)
                    _renderer.enabled = false;
                return;
            }

            if (_renderer != null)
                _renderer.enabled = true;

            float visual = radius - InsetWorld;
            if (visual < SphericalMapEcs.MinValidRadius)
                visual = radius * 0.92f;

            if (Mathf.Abs(visual - _appliedVisual) < 0.05f)
                return;

            _appliedVisual = visual;
            // Unity default sphere mesh is 1 unit diameter.
            if (_xf == null)
                _xf = transform;
            _xf.position = Vector3.zero;
            _xf.rotation = Quaternion.identity;
            _xf.localScale = Vector3.one * (visual * 2f);
        }

        void ApplyMaterial()
        {
            if (_renderer == null)
                _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null)
                return;

            Material mat = Resources.Load<Material>("Materials/TitanOrbitSpaceBackgroundScroll");
            if (mat == null)
                mat = Resources.Load<Material>("Materials/SpaceBackgroundURPUnlit");
            if (mat != null)
                mat = new Material(mat) { name = "SphereMapGlobeSpaceMat" };
            else
            {
                var shader = Shader.Find("TitanOrbit/SpaceBackgroundUnlit");
                if (shader == null)
                    shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                    shader = Shader.Find("Unlit/Texture");
                if (shader == null)
                    return;
                mat = new Material(shader) { name = "SphereMapGlobeSpaceMat" };
            }

            Texture2D tex = Resources.Load<Texture2D>("DinV_SpaceBackground");
            if (tex == null)
                tex = Resources.Load<Texture2D>("UI/Backgrounds/SpaceBackground");
            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Repeat;
                mat.mainTexture = tex;
                if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", tex);
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", tex);
            }

            if (mat.HasProperty("_UVScroll"))
                mat.SetVector("_UVScroll", new Vector4(2f, 1f, 0f, 0f));
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color.white);
            mat.color = Color.white;

            _renderer.sharedMaterial = mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }
    }
}
