using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Renders a tiled space background that scrolls as the camera (player ship) moves.
    /// Uses the DinV Dynamic Space Background Lite textures - assign any of the nebula/star
    /// textures for a seamless parallax effect.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class ScrollingSpaceBackground : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Camera to follow (defaults to Main Camera)")]
        [SerializeField] private UnityEngine.Camera targetCamera;

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
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapSt = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexSt = Shader.PropertyToID("_MainTex_ST");

        private void Awake()
        {
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
            // WebGL / late network init: Main Camera may not exist when Awake runs.
            if (targetCamera == null)
                ResolveTargetCamera();
            if (meshRenderer == null && targetCamera != null)
                EnsureBackgroundQuad();
        }

        private void ResolveTargetCamera()
        {
            if (targetCamera != null) return;
            targetCamera = UnityEngine.Camera.main;
        }

        private void EnsureBackgroundQuad()
        {
            if (meshRenderer != null) return;

            // Load default texture if none assigned
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

            // Force Repeat wrap mode - fixes smearing when DinV textures use Clamp (wrapU: 1)
            spaceTexture.wrapModeU = TextureWrapMode.Repeat;
            spaceTexture.wrapModeV = TextureWrapMode.Repeat;

            // Create material - use Unlit for consistent visibility
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Texture")
                ?? Shader.Find("Sprites/Default");
            if (unlitShader == null)
            {
                Debug.LogError("ScrollingSpaceBackground: Could not find a suitable shader.");
                return;
            }

            bgMaterial = new Material(unlitShader);
            ApplyTextureToMaterial(bgMaterial, spaceTexture);
            ApplyTextureScaleOffset(bgMaterial, new Vector4(textureTiling, textureTiling, 0f, 0f));
            bgMaterial.renderQueue = 1000; // Render behind most objects

            // Create quad as child - will follow camera XZ in LateUpdate
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "SpaceBackgroundQuad";
            quad.transform.SetParent(transform);

            // Horizontal plane (XZ) facing up for top-down camera
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float orthoSize = targetCamera.orthographicSize;
            float aspect = targetCamera.aspect > 0.01f ? targetCamera.aspect : (float)Screen.width / Mathf.Max(1, Screen.height);
            float visibleHeight = 2f * orthoSize;
            float visibleWidth = 2f * orthoSize * aspect;
            float quadSize = Mathf.Max(visibleWidth, visibleHeight) * sizeMargin;
            quad.transform.localScale = new Vector3(quadSize, quadSize, 1f);

            Object.Destroy(quad.GetComponent<Collider>()); // No physics needed

            meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = bgMaterial;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        private void LateUpdate()
        {
            ResolveTargetCamera();
            if (targetCamera == null || bgMaterial == null) return;

            // Follow camera XZ so background stays centered, place below game (Y negative)
            Vector3 camPos = targetCamera.transform.position;
            transform.position = new Vector3(camPos.x, -depthOffset, camPos.z);

            // Scroll texture based on world position - creates parallax as ship flies
            float offsetX = camPos.x * scrollScale;
            float offsetZ = camPos.z * scrollScale;
            ApplyTextureScaleOffset(bgMaterial, new Vector4(textureTiling, textureTiling, offsetX, offsetZ));
        }

        /// <summary>
        /// Sets tiling and offset on the material. Uses SetTextureScale/Offset so URP/SRP batching and WebGL
        /// apply UV scrolling reliably; raw _ST vectors alone can appear static in some player builds.
        /// </summary>
        private static void ApplyTextureToMaterial(Material m, Texture2D tex)
        {
            if (m == null || tex == null) return;
            m.mainTexture = tex;
            if (m.HasProperty(BaseMap)) m.SetTexture(BaseMap, tex);
            if (m.HasProperty(MainTex)) m.SetTexture(MainTex, tex);
        }

        private static void ApplyTextureScaleOffset(Material m, Vector4 tilingOffsetXy)
        {
            if (m == null) return;
            var scale = new Vector2(tilingOffsetXy.x, tilingOffsetXy.y);
            var offset = new Vector2(tilingOffsetXy.z, tilingOffsetXy.w);

            if (m.HasProperty(BaseMap))
            {
                m.SetTextureScale("_BaseMap", scale);
                m.SetTextureOffset("_BaseMap", offset);
            }

            if (m.HasProperty(MainTex))
            {
                m.SetTextureScale("_MainTex", scale);
                m.SetTextureOffset("_MainTex", offset);
            }

            if (m.HasProperty(BaseMapSt)) m.SetVector(BaseMapSt, tilingOffsetXy);
            if (m.HasProperty(MainTexSt)) m.SetVector(MainTexSt, tilingOffsetXy);
        }

        private void OnDestroy()
        {
            if (bgMaterial != null)
                Object.Destroy(bgMaterial);
        }

        /// <summary>
        /// Temporarily hide/show the background quad without destroying it. Used for galactic zoom-out.
        /// </summary>
        public void SetTemporarilyHidden(bool hidden)
        {
            if (meshRenderer == null)
            {
                EnsureBackgroundQuad();
            }

            if (meshRenderer != null)
            {
                meshRenderer.enabled = !hidden;
            }
        }
    }
}
