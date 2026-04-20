using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Renders a tiled space background that scrolls as the camera (player ship) moves.
    /// Uses the DinV Dynamic Space Background Lite textures - assign any of the nebula/star
    /// textures for a seamless parallax effect.
    /// </summary>
    /// <remarks>
    /// WebGL and Android GLES often fail to animate URP Unlit UVs when mixing MaterialPropertyBlock
    /// with <c>_BaseMap_ST</c> / SRP Batcher. This component uses the project shader
    /// <c>TitanOrbit/SpaceBackgroundUnlit</c> and drives scrolling only via <c>_UVScroll</c> on the
    /// instance material (no property block).
    /// </remarks>
    [DefaultExecutionOrder(300)]
    public class ScrollingSpaceBackground : MonoBehaviour
    {
        /// <summary>Resources material that references the scrolling background shader (included in player builds).</summary>
        private const string ScrollMaterialResourcePath = "Materials/TitanOrbitSpaceBackgroundScroll";

        private const string ScrollShaderName = "TitanOrbit/SpaceBackgroundUnlit";

        [Header("References")]
        [Tooltip("Camera to follow (defaults to Main Camera or Camera on CameraController)")]
        [SerializeField] private UnityEngine.Camera targetCamera;

        [Tooltip("Optional: resolves the same camera as gameplay. Prefer assigning on WebGL / multiplayer.")]
        [SerializeField] private CameraController cameraController;

        [Header("Texture")]
        [Tooltip("Space background texture - use Nebula Blue, Nebula Aqua-Pink, Nebula Red, Stars Small, or Stars Big from DinV asset. Must have Wrap Mode: Repeat.")]
        [SerializeField] private Texture2D spaceTexture;

        [Header("Scrolling")]
        [Tooltip("How fast the background scrolls relative to movement. 0.02 = subtle, 0.05 = noticeable")]
        [SerializeField] private float scrollScale = 0.03f;

        [Tooltip("Tiling - how many times the texture repeats across the visible area")]
        [SerializeField] private float textureTiling = 2f;

        [Header("Placement")]
        [Tooltip("Distance below camera for the background plane (further = more parallax)")]
        [SerializeField] private float depthOffset = 150f;
        [Tooltip("Extra margin beyond visible area to prevent edge gaps on wide screens")]
        [SerializeField] private float sizeMargin = 1.15f;

        private MeshRenderer meshRenderer;
        private Material bgMaterial;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int UVScroll = Shader.PropertyToID("_UVScroll");

        private void Awake()
        {
            ResolveCameraController();
            ResolveTargetCamera();
            if (targetCamera == null)
            {
                Debug.LogWarning("ScrollingSpaceBackground: No camera assigned and Main Camera not found.");
                return;
            }

            EnsureBackgroundQuad();
        }

        private void OnEnable()
        {
            ResolveCameraController();
            if (targetCamera == null)
                ResolveTargetCamera();
            if (meshRenderer == null && targetCamera != null)
                EnsureBackgroundQuad();
        }

        private void ResolveCameraController()
        {
            if (cameraController != null) return;
            if (targetCamera != null)
                cameraController = targetCamera.GetComponent<CameraController>();
            if (cameraController == null)
                cameraController = FindFirstObjectByType<CameraController>();
        }

        private void ResolveTargetCamera()
        {
            if (targetCamera != null) return;
            ResolveCameraController();
            if (cameraController != null)
            {
                targetCamera = cameraController.GetComponent<UnityEngine.Camera>();
                if (targetCamera == null)
                    targetCamera = cameraController.GetComponentInChildren<UnityEngine.Camera>();
            }
            if (targetCamera == null)
                targetCamera = UnityEngine.Camera.main;
        }

        private void EnsureBackgroundQuad()
        {
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

            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float orthoSize = targetCamera.orthographicSize;
            float aspect = targetCamera.aspect > 0.01f ? targetCamera.aspect : (float)Screen.width / Mathf.Max(1, Screen.height);
            float visibleHeight = 2f * orthoSize;
            float visibleWidth = 2f * orthoSize * aspect;
            float quadSize = Mathf.Max(visibleWidth, visibleHeight) * sizeMargin;
            quad.transform.localScale = new Vector3(quadSize, quadSize, 1f);

            Object.Destroy(quad.GetComponent<Collider>());

            meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = bgMaterial;
            meshRenderer.SetPropertyBlock(null);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        private void LateUpdate()
        {
            ResolveCameraController();
            ResolveTargetCamera();
            if (targetCamera == null) return;

            if (meshRenderer == null || bgMaterial == null)
                EnsureBackgroundQuad();
            if (bgMaterial == null) return;

            Vector3 camPos = targetCamera.transform.position;
            transform.position = new Vector3(camPos.x, -depthOffset, camPos.z);

            float offsetX = camPos.x * scrollScale;
            float offsetZ = camPos.z * scrollScale;
            bgMaterial.SetVector(UVScroll, new Vector4(textureTiling, textureTiling, offsetX, offsetZ));
        }

        private static void ApplyMainTexture(Material m, Texture2D tex)
        {
            if (m == null || tex == null) return;
            m.mainTexture = tex;
            if (m.HasProperty(MainTex))
                m.SetTexture(MainTex, tex);
        }

        private void OnDestroy()
        {
            if (bgMaterial != null)
                Object.Destroy(bgMaterial);
        }

        private static void TrySetTextureRepeatWrap(Texture2D tex)
        {
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
            if (meshRenderer == null)
                EnsureBackgroundQuad();

            if (meshRenderer != null)
                meshRenderer.enabled = !hidden;
        }
    }
}
