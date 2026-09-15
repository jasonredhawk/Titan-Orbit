using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Static star + gas quad for the Customize Ship preview camera.
    /// Same Resources material as the match starfield, but it does not follow the ship.
    /// Lives in TitanOrbit.Game so the studio overlay can reference it (Camera scripts
    /// are Assembly-CSharp and Game cannot see that assembly).
    /// </summary>
    public sealed class PreviewStarfieldBackdrop : MonoBehaviour
    {
        const string StarfieldMaterialResourcePath = "Materials/TitanOrbitParallaxStarfield";
        const string StarfieldShaderName = "TitanOrbit/ParallaxStarfield";

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        static readonly int TwinkleId = Shader.PropertyToID("_Twinkle");
        static readonly int FollowXZId = Shader.PropertyToID("_FollowXZ");
        static readonly int QuadScaleId = Shader.PropertyToID("_QuadScale");
        static readonly int OccupancyId = Shader.PropertyToID("_Occupancy");
        static readonly int LayerFarId = Shader.PropertyToID("_LayerFar");
        static readonly int LayerMidId = Shader.PropertyToID("_LayerMid");
        static readonly int LayerNearId = Shader.PropertyToID("_LayerNear");
        static readonly int GasTintAId = Shader.PropertyToID("_GasTintA");
        static readonly int GasTintBId = Shader.PropertyToID("_GasTintB");
        static readonly int GasIntensityId = Shader.PropertyToID("_GasIntensity");
        static readonly int GasOccludeId = Shader.PropertyToID("_GasOcclude");
        static readonly int GasScaleId = Shader.PropertyToID("_GasScale");
        static readonly int GasParallaxId = Shader.PropertyToID("_GasParallax");

        /// <summary>World units of starfield across the preview — smaller than the match quad so stars read closer.</summary>
        const float PreviewLookScale = 96f;

        Camera _camera;
        Material _material;
        Transform _quad;
        Vector2 _followXz;
        bool _copiedLook;

        /// <summary>Builds a ShipPreview-layer quad behind the studio hull.</summary>
        public static PreviewStarfieldBackdrop Create(Transform parent, Camera cam, int layer)
        {
            if (parent == null || cam == null)
                return null;

            Transform existing = parent.Find("PreviewStarfield");
            if (existing != null)
            {
                var reuse = existing.GetComponent<PreviewStarfieldBackdrop>();
                if (reuse != null)
                {
                    reuse._camera = cam;
                    reuse.FitToCamera();
                    return reuse;
                }
            }

            var root = new GameObject("PreviewStarfield");
            root.transform.SetParent(parent, false);
            root.layer = layer;
            var backdrop = root.AddComponent<PreviewStarfieldBackdrop>();
            backdrop._camera = cam;
            backdrop.BuildQuad(layer);
            backdrop.FitToCamera();
            return backdrop;
        }

        void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }

        void LateUpdate()
        {
            FitToCamera();
        }

        /// <summary>Ship travel for parallax — same FollowXZ the match starfield uses.</summary>
        public void SetTravel(float worldX, float worldZ)
        {
            _followXz = new Vector2(worldX, worldZ);
        }

        void BuildQuad(int layer)
        {
            Material template = Resources.Load<Material>(StarfieldMaterialResourcePath);
            if (template != null)
                _material = new Material(template);
            else
            {
                Shader shader = Shader.Find(StarfieldShaderName);
                if (shader == null)
                    return;
                _material = new Material(shader);
            }

            _material.SetOverrideTag("RenderType", "Opaque");
            _material.SetFloat("_Surface", 0f);
            _material.renderQueue = 1001;

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Quad";
            quad.layer = layer;
            quad.transform.SetParent(transform, false);
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Destroy(quad.GetComponent<Collider>());

            var rend = quad.GetComponent<MeshRenderer>();
            rend.sharedMaterial = _material;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            _quad = quad.transform;
        }

        /// <summary>
        /// Covers the ortho preview, then copies the live match starfield look.
        /// Shader density uses <c>_QuadScale</c> (world units), not the mesh size —
        /// a tiny studio quad with the game's scale reads as the same field.
        /// </summary>
        public void FitToCamera()
        {
            if (_camera == null || _quad == null || _material == null)
                return;

            float aspect = _camera.aspect > 0.01f ? _camera.aspect : 1f;
            float visibleHeight = _camera.orthographic
                ? 2f * _camera.orthographicSize
                : 16f;
            float meshSize = Mathf.Max(visibleHeight * aspect, visibleHeight) * 1.35f;
            Vector3 camPos = _camera.transform.position;
            transform.position = new Vector3(camPos.x, camPos.y - 36f, camPos.z);
            _quad.localPosition = Vector3.zero;
            _quad.localScale = new Vector3(meshSize, meshSize, 1f);

            if (!_copiedLook)
            {
                Material live = FindLiveStarMaterial();
                if (live != null)
                    _material.CopyPropertiesFromMaterial(live);
                else
                    ApplyFallbackLook();
                _material.SetOverrideTag("RenderType", "Opaque");
                _material.renderQueue = 1001;
                _copiedLook = true;
            }

            float driftX = _followXz.x * 1.15f + Time.unscaledTime * 0.28f;
            float driftZ = _followXz.y * 1.15f + Time.unscaledTime * 0.12f;
            _material.SetVector(FollowXZId, new Vector4(driftX, driftZ, 0f, 0f));
            _material.SetVector(QuadScaleId, new Vector4(PreviewLookScale, PreviewLookScale, 0f, 0f));
        }

        void ApplyFallbackLook()
        {
            _material.SetColor(TintId, Color.white);
            _material.SetFloat(BrightnessId, 0.9f);
            _material.SetFloat(TwinkleId, 0.18f);
            _material.SetVector(OccupancyId, new Vector4(0.55f, 0.38f, 0.28f, 0f));
            _material.SetVector(LayerFarId, new Vector4(0.42f, 0.0022f, 0.028f, 0.45f));
            _material.SetVector(LayerMidId, new Vector4(0.22f, 0.005f, 0.038f, 0.7f));
            _material.SetVector(LayerNearId, new Vector4(0.11f, 0.01f, 0.05f, 0.95f));
            _material.SetColor(GasTintAId, new Color(0.32f, 0.42f, 0.82f, 1f));
            _material.SetColor(GasTintBId, new Color(0.58f, 0.26f, 0.52f, 1f));
            _material.SetFloat(GasIntensityId, 0.36f);
            _material.SetFloat(GasOccludeId, 0.42f);
            _material.SetVector(GasScaleId, new Vector4(0.024f, 0.038f, 0f, 0f));
            _material.SetVector(GasParallaxId, new Vector4(0.014f, 0.022f, 0f, 0f));
        }

        static MeshRenderer s_LiveStarRenderer;

        static Material FindLiveStarMaterial()
        {
            if (s_LiveStarRenderer == null)
            {
                GameObject quad = GameObject.Find("StarfieldBackgroundQuad");
                if (quad == null)
                    return null;
                s_LiveStarRenderer = quad.GetComponent<MeshRenderer>();
            }

            return s_LiveStarRenderer != null ? s_LiveStarRenderer.sharedMaterial : null;
        }
    }
}
