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
        bool _scanShell;
        Renderer _scanRenderer;
        MaterialPropertyBlock _scanBlock;
        float _scanAlpha = -1f;

        static Material s_ScanMaterial;
        static int s_ScanScrollFrame = -1;

        /// <summary>
        /// Particle start size on MatrixShield. The mesh is drawn directly, so this
        /// scale replaces that particle size.
        /// </summary>
        const float ScanShellParticleSize = 2f;

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

            GameObject prefab = GemMoonShieldPrefabLibrary.GetPrefab(_team);
            if (prefab == null)
                return;

            // The prefab's shell is one mesh (scan-line texture on scifi_shield). Its other
            // systems are soft round glow sprites. Draw the mesh itself on every client,
            // including the Editor — one shared surface per moon, no 1000-particle reservation.
            if (TryCreateScanShell(prefab))
                return;

            // Awake reserves maxParticles immediately. Parent under an inactive holder
            // so a smaller cap is what the looping systems allocate.
            GameObject hold = null;
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                hold = new GameObject("ShieldHold");
                hold.SetActive(false);
                _shieldInstance = Instantiate(prefab, hold.transform);
                PrepareWebGlShieldLoop(_shieldInstance);
            }
            else
            {
                _shieldInstance = Instantiate(prefab, transform);
            }

            _shieldInstance.transform.localPosition = Vector3.zero;
            VfxUrpCompat.RetargetBuiltInParticlesForWebGl(_shieldInstance);
            if (hold != null)
            {
                _shieldInstance.transform.SetParent(transform, false);
                Destroy(hold);
            }
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

            if (!shouldBeActive)
                return;

            float ratio = Mathf.Clamp01(shieldRatio);
            if (_scanShell)
            {
                TickScanScroll();
                ApplyScanTint(ratio);
                return;
            }

            if (_particles == null)
                return;

            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null)
                    continue;
                var emission = _particles[i].emission;
                emission.rateOverTimeMultiplier = ratio;
            }
        }

        /// <summary>
        /// Builds the matrix shell from the prefab's mesh particle, without spawning
        /// the glow billboards that read as a plain circle.
        /// </summary>
        bool TryCreateScanShell(GameObject prefab)
        {
            // Inactive parent: ParticleSystem.Awake must not run, or it reserves 1000 slots.
            var hold = new GameObject("ShieldHold");
            hold.SetActive(false);
            GameObject instance = Instantiate(prefab, hold.transform);
            var particleRenderer = instance.GetComponent<ParticleSystemRenderer>();
            Mesh mesh = particleRenderer != null ? particleRenderer.mesh : null;
            Material source = particleRenderer != null ? particleRenderer.sharedMaterial : null;
            if (mesh == null || !EnsureScanMaterial(source))
            {
                Destroy(instance);
                Destroy(hold);
                return false;
            }

            // Glow children are soft round sprites. Drop them and the particle systems
            // before the shell is activated, so only the scan mesh remains.
            for (int i = instance.transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(instance.transform.GetChild(i).gameObject);

            if (particleRenderer != null)
                DestroyImmediate(particleRenderer);

            ParticleSystem[] systems = instance.GetComponents<ParticleSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    DestroyImmediate(systems[i]);
            }

            var filter = instance.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            _scanRenderer = instance.AddComponent<MeshRenderer>();
            _scanRenderer.sharedMaterial = s_ScanMaterial;
            _scanRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _scanRenderer.receiveShadows = false;

            instance.transform.SetParent(transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = prefab.transform.localRotation;
            instance.transform.localScale = Vector3.one * ScanShellParticleSize;
            Destroy(hold);

            _scanShell = true;
            _shieldInstance = instance;
            _baseLocalRotation = instance.transform.localRotation;
            Vector3 baseScale = instance.transform.localScale;
            _baseXScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.x));
            _baseYScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.y));
            _lastDockLocalRadius = -1f;
            return true;
        }

        static bool EnsureScanMaterial(Material source)
        {
            if (s_ScanMaterial != null)
                return true;

            Texture scan = null;
            if (source != null)
                scan = source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : source.mainTexture;
            if (scan == null)
                return false;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                return false;

            s_ScanMaterial = new Material(shader);
            s_ScanMaterial.name = "MatrixShieldScan";
            if (s_ScanMaterial.HasProperty("_BaseMap"))
                s_ScanMaterial.SetTexture("_BaseMap", scan);
            if (s_ScanMaterial.HasProperty("_MainTex"))
                s_ScanMaterial.SetTexture("_MainTex", scan);
            if (s_ScanMaterial.HasProperty("_Surface"))
                s_ScanMaterial.SetFloat("_Surface", 1f);
            if (s_ScanMaterial.HasProperty("_Blend"))
                s_ScanMaterial.SetFloat("_Blend", 2f);
            s_ScanMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            s_ScanMaterial.SetInt("_DstBlend", (int)BlendMode.One);
            s_ScanMaterial.SetInt("_ZWrite", 0);
            s_ScanMaterial.SetInt("_Cull", (int)CullMode.Off);
            s_ScanMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            s_ScanMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            s_ScanMaterial.SetOverrideTag("RenderType", "Transparent");
            s_ScanMaterial.renderQueue = (int)RenderQueue.Transparent;
            s_ScanMaterial.SetShaderPassEnabled("ShadowCaster", false);
            return true;
        }

        /// <summary>One offset write per frame, shared by every moon.</summary>
        static void TickScanScroll()
        {
            if (s_ScanMaterial == null || s_ScanScrollFrame == Time.frameCount)
                return;

            s_ScanScrollFrame = Time.frameCount;
            Vector2 offset = new Vector2(0f, Time.time * 0.35f);
            s_ScanMaterial.SetTextureOffset("_BaseMap", offset);
            if (s_ScanMaterial.HasProperty("_MainTex"))
                s_ScanMaterial.SetTextureOffset("_MainTex", offset);
        }

        void ApplyScanTint(float ratio)
        {
            if (_scanRenderer == null)
                return;
            if (_scanBlock != null && Mathf.Abs(ratio - _scanAlpha) < 0.01f)
                return;

            _scanAlpha = ratio;
            if (_scanBlock == null)
                _scanBlock = new MaterialPropertyBlock();

            Color color = _team.ToColor();
            color.a = 0.85f * Mathf.Lerp(0.45f, 1f, ratio);
            _scanBlock.SetColor("_BaseColor", color);
            _scanBlock.SetColor("_Color", color);
            _scanRenderer.SetPropertyBlock(_scanBlock);
        }

        /// <summary>
        /// Fallback when the shield mesh cannot be read. Caps the looping prefab so
        /// WebGL does not reserve 1000 particles per system.
        /// </summary>
        static void PrepareWebGlShieldLoop(GameObject root)
        {
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                    continue;

                var renderer = system.GetComponent<ParticleSystemRenderer>();
                int cap = renderer != null && renderer.renderMode == ParticleSystemRenderMode.Mesh
                    ? 8
                    : 48;

                var main = system.main;
                main.loop = true;
                if (main.maxParticles > cap)
                    main.maxParticles = cap;
                main.cullingMode = ParticleSystemCullingMode.Automatic;

                if (renderer == null)
                    continue;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
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
            _scanRenderer = null;
            _scanBlock = null;
            _scanAlpha = -1f;
            _scanShell = false;
            _lastDockLocalRadius = -1f;
        }
    }
}
