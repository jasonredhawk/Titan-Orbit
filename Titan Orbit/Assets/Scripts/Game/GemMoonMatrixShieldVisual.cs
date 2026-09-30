using TitanOrbit.Core;
using TitanOrbit.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>Team-colored matrix shield VFX around the gem moon (legacy PlanetGemMoon matrix shield).</summary>
    public class GemMoonMatrixShieldVisual : MonoBehaviour
    {
        const string ShieldRootName = "GemMoonMatrixShield";

        [SerializeField] float matrixShieldScaleMultiplier = 1f;

        PlanetGemMoonVisualProxy _moon;
        TeamId _team = TeamId.None;
        GameObject _shieldInstance;
        Quaternion _baseLocalRotation = Quaternion.identity;
        float _baseXScale = 1f;
        float _baseYScale = 1f;
        float _lastDockLocalRadius = -1f;
        ParticleSystem[] _particles;
        bool _lightweight;

        static Mesh s_SphereMesh;
        static Material[] s_LightweightMaterials;

        public void Configure(PlanetGemMoonVisualProxy moon, TeamId team)
        {
            // --- Configure ---
            _moon = moon;
            if (_team != team)
            {
                _team = team;
                DestroyShieldInstance();
            }
        }

        public static GemMoonMatrixShieldVisual EnsureOnMoonRoot(Transform moonRoot, PlanetGemMoonVisualProxy moon, TeamId team)
        {
            // --- Ensure setup ---
            Transform existing = moonRoot.Find(ShieldRootName);
            GameObject shieldGo;
            if (existing != null)
                shieldGo = existing.gameObject;
            else
            {
                shieldGo = new GameObject(ShieldRootName);
                shieldGo.transform.SetParent(moonRoot, false);
            }

            var visual = shieldGo.GetComponent<GemMoonMatrixShieldVisual>();
            if (visual == null)
                visual = shieldGo.AddComponent<GemMoonMatrixShieldVisual>();
            visual.Configure(moon, team);
            return visual;
        }

        void OnDestroy()
        {
            DestroyShieldInstance();
        }

        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (_moon == null)
                return;

            UpdateShieldVisual(_moon.CurrentShieldRatio);
        }

        void EnsureShieldInstance()
        {
            // --- Ensure setup ---
            if (_shieldInstance != null)
                return;

            // Sci-Fi MatrixShield is three mesh-particle systems reserved at 1000 each.
            // Twenty-four moons blow the WebGL heap (abortOnCannotGrowMemory). A shared
            // unlit sphere is one mesh and one material per team.
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                _lightweight = true;
                _shieldInstance = CreateLightweightShield(_team);
                _shieldInstance.transform.SetParent(transform, false);
                _shieldInstance.transform.localPosition = Vector3.zero;
                _baseLocalRotation = Quaternion.identity;
                _baseXScale = 1f;
                _baseYScale = 1f;
                _lastDockLocalRadius = -1f;
                _particles = System.Array.Empty<ParticleSystem>();
                return;
            }

            GameObject prefab = GemMoonShieldPrefabLibrary.GetPrefab(_team);
            if (prefab == null)
                return;

            _shieldInstance = Instantiate(prefab, transform);
            _shieldInstance.transform.localPosition = Vector3.zero;
            VfxUrpCompat.RetargetBuiltInParticlesForWebGl(_shieldInstance);
            _baseLocalRotation = _shieldInstance.transform.localRotation;

            Vector3 baseScale = _shieldInstance.transform.localScale;
            _baseXScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.x));
            _baseYScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.y));
            _lastDockLocalRadius = -1f;
            _particles = _shieldInstance.GetComponentsInChildren<ParticleSystem>(true);
        }

        void UpdateShieldVisual(float shieldRatio)
        {
            // --- Per-frame refresh ---
            EnsureShieldInstance();
            if (_shieldInstance == null)
                return;

            if (_lightweight)
            {
                float bubble = Mathf.Clamp01(shieldRatio);
                bool show = bubble > 0.001f;
                if (_shieldInstance.activeSelf != show)
                    _shieldInstance.SetActive(show);
                if (!show)
                    return;

                // Unity sphere radius is 0.5, so scale = diameter = shield radius * 2.
                float radius = Mathf.Max(0.05f, _moon.MoonShieldOuterRadiusLocal);
                float size = radius * 2f * Mathf.Lerp(0.35f, 1f, bubble);
                _shieldInstance.transform.localScale = new Vector3(size, size, size);
                return;
            }

            Vector3 axisLocal = transform.InverseTransformDirection(_moon.SpinAxisWorld);
            if (axisLocal.sqrMagnitude < 0.0001f)
                axisLocal = Vector3.up;
            axisLocal.Normalize();
            Quaternion alignToMoonAxis = Quaternion.FromToRotation(Vector3.up, axisLocal);
            _shieldInstance.transform.localRotation = alignToMoonAxis * _baseLocalRotation;

            float shellOuterLocal = _moon.MoonVisualShellOuterRadiusLocal;
            if (_lastDockLocalRadius < 0f || Mathf.Abs(shellOuterLocal - _lastDockLocalRadius) > 0.001f)
            {
                _lastDockLocalRadius = shellOuterLocal;
                float denom = Mathf.Max(0.001f, PlanetGemMoonMath.MatrixShieldRadiusReference);
                float scaleMultiplier = (shellOuterLocal * PlanetGemMoonMath.MatrixShieldOrbitZoneEdgeExpandMultiplier / denom)
                    * matrixShieldScaleMultiplier;
                float scaleX = _baseXScale * scaleMultiplier;
                float scaleY = _baseYScale * scaleMultiplier;
                _shieldInstance.transform.localScale = new Vector3(scaleX, scaleY, scaleX);
            }

            bool shouldBeActive = shieldRatio > 0.001f;
            if (_shieldInstance.activeSelf != shouldBeActive)
                _shieldInstance.SetActive(shouldBeActive);

            if (!shouldBeActive || _particles == null)
                return;

            float ratio = Mathf.Clamp01(shieldRatio);
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null)
                    continue;
                var emission = _particles[i].emission;
                emission.rateOverTimeMultiplier = ratio;
            }
        }

        /// <summary>
        /// One shared sphere mesh and one additive material per team. No particle buffers.
        /// </summary>
        static GameObject CreateLightweightShield(TeamId team)
        {
            if (s_SphereMesh == null)
            {
                var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s_SphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                if (Application.isPlaying)
                    Destroy(temp);
                else
                    DestroyImmediate(temp);
            }

            var go = new GameObject("GemMoonShieldWebGL");
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = s_SphereMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = LightweightMaterial(team);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        static Material LightweightMaterial(TeamId team)
        {
            int slot = (int)team;
            if (slot < 0 || slot > 5)
                slot = 0;
            if (s_LightweightMaterials == null)
                s_LightweightMaterials = new Material[6];
            if (s_LightweightMaterials[slot] != null)
                return s_LightweightMaterials[slot];

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var mat = new Material(shader);
            mat.name = "GemMoonShieldWebGL_" + team;
            Color color = team.ToColor();
            color.a = 0.32f;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 2f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_ALPHAMODULATE_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.renderQueue = (int)RenderQueue.Transparent;
            s_LightweightMaterials[slot] = mat;
            return mat;
        }

        void DestroyShieldInstance()
        {
            // --- DestroyShieldInstance ---
            if (_shieldInstance != null)
            {
                if (Application.isPlaying)
                    Destroy(_shieldInstance);
                else
                    DestroyImmediate(_shieldInstance);
                _shieldInstance = null;
            }

            _particles = null;
            _lightweight = false;
            _lastDockLocalRadius = -1f;
        }
    }
}
