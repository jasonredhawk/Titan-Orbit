using TitanOrbit.Core;
using TitanOrbit.Game;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Renders a tiled space background that scrolls as the camera (player ship) moves.
    /// Uses DinV Dynamic Space Background Lite textures — assign nebula/star textures for
    /// seamless parallax. [HYBRID] Follows <see cref="ShipDisplayPose.LocalPosition"/> when
    /// available, else camera position. WebGL/Android: uses TitanOrbit/SpaceBackgroundUnlit
    /// and _UVScroll on instance material (no MaterialPropertyBlock) for GLES compatibility.
    /// GameManager → Show Space Background hides this quad independently of the shader
    /// starfield so both can stay on, or either can run alone.
    /// </summary>
    /// <remarks>
    /// WebGL and Android GLES often fail to animate URP Unlit UVs when mixing MaterialPropertyBlock
    /// with <c>_BaseMap_ST</c> / SRP Batcher. This component uses the project shader
    /// <c>TitanOrbit/SpaceBackgroundUnlit</c> and drives scrolling only via <c>_UVScroll</c> on the
    /// instance material (no property block).
    /// </remarks>
    [DefaultExecutionOrder(67100)]
    public class ScrollingSpaceBackground : MonoBehaviour
    {
        /// <summary>Resources material that references the scrolling background shader (included in player builds).</summary>
        private const string ScrollMaterialResourcePath = "Materials/TitanOrbitSpaceBackgroundScroll";

        private const string ScrollShaderName = "TitanOrbit/SpaceBackgroundUnlit";

        [Header("References")]
        [Tooltip("Camera to follow (defaults to Main Camera)")]
        [SerializeField] private UnityEngine.Camera targetCamera;

        [Header("Texture")]
        [Tooltip("Space background texture - use Nebula Blue, Nebula Aqua-Pink, Nebula Red, Stars Small, or Stars Big from DinV asset. Must have Wrap Mode: Repeat.")]
        [SerializeField] private Texture2D spaceTexture;

        [Header("Scrolling")]
        [Tooltip("How fast the background scrolls relative to movement. 0.02 = subtle, 0.05 = noticeable")]
        [SerializeField] private float scrollScale = 0.01f;

        [Tooltip("Tiling - how many times the texture repeats across the visible area")]
        [SerializeField] private float textureTiling = 2f;

        [Header("Placement")]
        [Tooltip("How far in front of the lens the sky plane sits (world units). Same camera-forward placement as the shader starfield.")]
        [SerializeField] private float depthOffset = 400f;
        [Tooltip("Extra margin beyond visible area to prevent edge gaps on wide screens")]
        [SerializeField] private float sizeMargin = 1.35f;

        private MeshRenderer meshRenderer;
        private Material bgMaterial;
        private Transform backgroundQuadTransform;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int UVScroll = Shader.PropertyToID("_UVScroll");

        private bool hasLastScrollPos;
        private Vector3 lastScrollPos;
        private float scrollOffsetX;
        private float scrollOffsetZ;

        private void Awake()
        {
            // --- Unity lifecycle ---
            // Headless dedicated (Edgegap/GCE): no shaders in the server player. Creating a
            // background quad only logs "Dedicated Server Optimizations" and wastes a frame.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                enabled = false;
                return;
            }

            // Subscribe after the headless bail so a GameManager tick cannot re-enable this
            // on a dedicated server. Unsubscribe only in OnDestroy — enabled=false must not
            // drop the listener we need to turn the nebula back on.
            GameManager.ShowSpaceBackgroundChanged += OnShowSpaceBackgroundChanged;

            ResolveTargetCamera();
            if (targetCamera == null)
            {
                Debug.LogWarning("ScrollingSpaceBackground: No camera assigned and Main Camera not found.");
                return;
            }

            EnsureBackgroundQuad();
        }

        /// <summary>
        /// [UNITY] Start — sync with GameManager if its Awake fired before we subscribed.
        /// </summary>
        private void Start()
        {
            ApplyFeatureEnabled(GameManager.IsShowSpaceBackgroundActive);
        }

        private void OnEnable()
        {
            // --- Unity lifecycle ---
            if (targetCamera == null)
                ResolveTargetCamera();
            if (meshRenderer == null && targetCamera != null)
                EnsureBackgroundQuad();
            if (meshRenderer != null && GameManager.IsShowSpaceBackgroundActive)
                meshRenderer.enabled = true;
        }

        /// <summary>
        /// [UNITY] OnDisable — hide the nebula so a disabled component never leaves a leftover draw.
        /// The quad stays in the hierarchy so turning the GameManager toggle back on is free.
        /// </summary>
        private void OnDisable()
        {
            if (meshRenderer != null)
                meshRenderer.enabled = false;
        }

        /// <summary>
        /// GameManager published a new Show Space Background value (Play Mode Inspector or Awake).
        /// </summary>
        /// <param name="show">True when the nebula quad should draw and LateUpdate.</param>
        void OnShowSpaceBackgroundChanged(bool show) => ApplyFeatureEnabled(show);

        /// <summary>
        /// Turns the nebula fully on or off. Off hides the renderer and sets
        /// <c>enabled = false</c> so Unity skips LateUpdate. The quad is kept so a later
        /// toggle-on does not Instantiate again.
        /// </summary>
        /// <param name="show">GameManager toggle value.</param>
        void ApplyFeatureEnabled(bool show)
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                if (enabled)
                    enabled = false;
                return;
            }

            if (!show)
            {
                if (meshRenderer != null)
                    meshRenderer.enabled = false;
                if (enabled)
                    enabled = false;
                return;
            }

            if (!enabled)
                enabled = true;

            // Skip the idle frames so we do not add a huge UV jump when the toggle comes back.
            hasLastScrollPos = false;

            if (meshRenderer == null)
                EnsureBackgroundQuad();
            if (meshRenderer != null)
                meshRenderer.enabled = true;
        }

        private void ResolveTargetCamera()
        {
            if (targetCamera != null) return;
            targetCamera = UnityEngine.Camera.main;
        }

        private void EnsureBackgroundQuad()
        {
            // --- Ensure setup ---
            if (meshRenderer != null) return;

            if (spaceTexture == null)
            {
                spaceTexture = Resources.Load<Texture2D>("DinV_SpaceBackground");
                if (spaceTexture == null)
                {
#if UNITY_EDITOR
                    spaceTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/DinV/Dynamic Space Background/Sprites/Nebula Blue.png");
#endif
                }
            }

            if (spaceTexture == null)
            {
                Debug.LogWarning("ScrollingSpaceBackground: No space texture assigned. Assign a texture from Assets/DinV/Dynamic Space Background/Sprites/");
                return;
            }

            TrySetTextureRepeatWrap(spaceTexture);

            Material template = Resources.Load<Material>(ScrollMaterialResourcePath);
            if (template != null)
                bgMaterial = new Material(template);
            else
            {
                Shader scrollShader = Shader.Find(ScrollShaderName);
                if (scrollShader == null)
                {
                    Debug.LogError(
                        "ScrollingSpaceBackground: Missing Resources/" + ScrollMaterialResourcePath +
                        " and shader \"" + ScrollShaderName + "\" was not found. Add the Resources material.");
                    return;
                }

                bgMaterial = new Material(scrollShader);
            }

            ApplyMainTexture(bgMaterial, spaceTexture);
            bgMaterial.SetVector(UVScroll, new Vector4(textureTiling, textureTiling, 0f, 0f));
            bgMaterial.renderQueue = 1000;

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "SpaceBackgroundQuad";
            quad.transform.SetParent(transform);
            backgroundQuadTransform = quad.transform;

            backgroundQuadTransform.localRotation = Quaternion.identity;
            ResizeQuadToCoverView();

            Object.Destroy(quad.GetComponent<Collider>());

            meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = bgMaterial;
            meshRenderer.SetPropertyBlock(null);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = GameManager.IsShowSpaceBackgroundActive;
        }

        private void LateUpdate()
        {
            // --- Per-frame refresh ---
            ResolveTargetCamera();
            if (targetCamera == null) return;

            if (meshRenderer == null || bgMaterial == null)
                EnsureBackgroundQuad();
            if (bgMaterial == null) return;

            Vector3 followPos;
            if (ShipDisplayPose.HasLocalPose)
                followPos = ShipDisplayPose.LocalPosition;
            else if (targetCamera != null)
                followPos = targetCamera.transform.position;
            else
                return;

            PlaceSkyPlaneInFrontOfCamera();
            ResizeQuadToCoverView();

            if (!hasLastScrollPos)
            {
                lastScrollPos = followPos;
                hasLastScrollPos = true;
                scrollOffsetX = followPos.x * scrollScale;
                scrollOffsetZ = followPos.z * scrollScale;
            }
            else
            {
                scrollOffsetX += (followPos.x - lastScrollPos.x) * scrollScale;
                scrollOffsetZ += (followPos.z - lastScrollPos.z) * scrollScale;
                lastScrollPos = followPos;
            }

            bgMaterial.SetVector(UVScroll, new Vector4(textureTiling, textureTiling, scrollOffsetX, scrollOffsetZ));
        }

        /// <summary>
        /// Pins the nebula quad as a sky plane in front of the lens (same contract as
        /// <see cref="ParallaxStarfieldBackground"/>). One existing quad — no extra draws.
        /// </summary>
        private void PlaceSkyPlaneInFrontOfCamera()
        {
            Transform camT = targetCamera.transform;
            float dist = Mathf.Max(20f, Mathf.Abs(depthOffset));
            dist = Mathf.Min(dist, targetCamera.farClipPlane * 0.92f);
            dist = Mathf.Max(dist, targetCamera.nearClipPlane + 1f);
            transform.SetPositionAndRotation(
                camT.position + camT.forward * dist,
                Quaternion.LookRotation(-camT.forward, camT.up));

            if (backgroundQuadTransform != null)
                backgroundQuadTransform.localRotation = Quaternion.identity;
        }

        private void ResizeQuadToCoverView()
        {
            // --- ResizeQuadToCoverView ---
            if (targetCamera == null || backgroundQuadTransform == null)
                return;

            float aspect = targetCamera.aspect > 0.01f
                ? targetCamera.aspect
                : (float)Screen.width / Mathf.Max(1, Screen.height);

            float visibleHeight;
            if (targetCamera.orthographic)
            {
                visibleHeight = 2f * targetCamera.orthographicSize;
            }
            else
            {
                float cameraToBackground = Vector3.Distance(targetCamera.transform.position, transform.position);
                if (cameraToBackground < 1f)
                    cameraToBackground = Mathf.Abs(depthOffset);
                float halfFovRadians = targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                visibleHeight = 2f * cameraToBackground * Mathf.Tan(halfFovRadians);
            }

            float visibleWidth = visibleHeight * aspect;
            float quadSize = Mathf.Max(visibleWidth, visibleHeight) * sizeMargin;
            backgroundQuadTransform.localScale = new Vector3(quadSize, quadSize, 1f);
        }

        private static void ApplyMainTexture(Material m, Texture2D tex)
        {
            // --- Apply changes ---
            if (m == null || tex == null) return;
            m.mainTexture = tex;
            if (m.HasProperty(MainTex))
                m.SetTexture(MainTex, tex);
        }

        private void OnDestroy()
        {
            GameManager.ShowSpaceBackgroundChanged -= OnShowSpaceBackgroundChanged;
            if (bgMaterial != null)
                Object.Destroy(bgMaterial);
        }

        private static void TrySetTextureRepeatWrap(Texture2D tex)
        {
            // --- Attempt resolution ---
            if (tex == null) return;
            try
            {
                tex.wrapModeU = TextureWrapMode.Repeat;
                tex.wrapModeV = TextureWrapMode.Repeat;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(
                    "ScrollingSpaceBackground: Could not set texture wrap to Repeat at runtime; ensure the texture import uses Repeat wrap. " +
                    e.Message);
            }
        }

        /// <summary>
        /// Temporarily hide/show the background quad without destroying it. Used for galactic zoom-out.
        /// </summary>
        public void SetTemporarilyHidden(bool hidden)
        {
            // --- SetTemporarilyHidden ---
            if (meshRenderer == null)
                EnsureBackgroundQuad();

            if (meshRenderer != null)
                meshRenderer.enabled = !hidden;
        }
    }
}
