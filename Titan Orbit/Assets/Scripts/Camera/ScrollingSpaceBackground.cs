using TitanOrbit.Game;
using TitanOrbit.Shared;
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
        [Tooltip("Distance below the gameplay plane in world Y (further down = safer behind planets/ships)")]
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

            backgroundQuadTransform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ResizeQuadToCoverView();

            Object.Destroy(quad.GetComponent<Collider>());

            meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = bgMaterial;
            meshRenderer.SetPropertyBlock(null);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        private void LateUpdate()
        {
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

            transform.position = new Vector3(followPos.x, -Mathf.Abs(depthOffset), followPos.z);
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

        private void ResizeQuadToCoverView()
        {
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
                float backgroundY = -Mathf.Abs(depthOffset);
                float cameraToBackground = Mathf.Abs(targetCamera.transform.position.y - backgroundY);
                float halfFovRadians = targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                visibleHeight = 2f * cameraToBackground * Mathf.Tan(halfFovRadians);
            }

            float visibleWidth = visibleHeight * aspect;
            float quadSize = Mathf.Max(visibleWidth, visibleHeight) * sizeMargin;
            backgroundQuadTransform.localScale = new Vector3(quadSize, quadSize, 1f);
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
