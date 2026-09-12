using TitanOrbit.Core;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Camera
{
    /// <summary>
    /// Client-only shader starfield that sits behind gameplay and shifts as the local ship flies.
    /// Three hash-drawn layers (far / mid / near) use different parallax scales so nearer stars
    /// slide faster — the cheap “flying through space” feel without a Unity ParticleSystem.
    /// All three layers share one Background-queue quad so they stay behind ships, planets,
    /// and other gameplay opaques.
    /// <para>
    /// Lives on the <c>StarfieldBackground</c> scene object (sibling of <c>SpaceBackground</c>,
    /// not a child — <see cref="ScrollingSpaceBackground"/> writes that parent’s world Y).
    /// Follows <see cref="ShipDisplayPose.LocalPosition"/> after the camera
    /// (<c>DefaultExecutionOrder 67110</c>). GameManager → Show Starfield Background turns this
    /// on or off independently of the nebula; both can draw at once (additive stars over the quad).
    /// Headless dedicated servers skip setup via <see cref="TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation"/>.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67110)]
    public class ParallaxStarfieldBackground : MonoBehaviour
    {
        /// <summary>Resources material that references the starfield shader (included in player builds).</summary>
        const string StarfieldMaterialResourcePath = "Materials/TitanOrbitParallaxStarfield";

        /// <summary>Fallback shader name when the Resources material is missing.</summary>
        const string StarfieldShaderName = "TitanOrbit/ParallaxStarfield";

        /// <summary>
        /// If map size is unknown, treat a one-frame move larger than this as a torus wrap snap
        /// and do not add it to the parallax scroll (avoids a starfield yank).
        /// </summary>
        const float WrapSnapFallbackThreshold = 80f;

        [Header("References")]
        [Tooltip("Camera used to size the star quad (defaults to Main Camera).")]
        [SerializeField] UnityEngine.Camera targetCamera;

        [Header("Placement")]
        [Tooltip("World-Y depth below the play plane. Keep this larger than the biggest planet radius (~20) so the quad does not slice through worlds. The shader draws in the Background queue (ZTest Always, ZWrite Off) so stars still composite over the nebula and stay behind ships / planets.")]
        [SerializeField] float depthOffset = 80f;

        [Tooltip("Extra margin beyond the visible area so zoom / wide aspects do not show a gap.")]
        [SerializeField] float sizeMargin = 1.35f;

        [Header("Look")]
        [Tooltip("Star color tint (usually cool white). Multiplies the hashed brightness.")]
        [SerializeField] Color tint = Color.white;

        [Tooltip("Master brightness. 1 = designed defaults; raise if stars vanish on a bright nebula.")]
        [SerializeField] float brightness = 1f;

        [Tooltip("0 = static stars. ~0.18 = a gentle twinkle. Keep low — this is a multiply in the shader, not extra draws.")]
        [SerializeField] [Range(0f, 1f)] float twinkle = 0.18f;

        [Header("Far layer (deepest, almost still)")]
        [Tooltip("Cells per world unit. Higher = more tiny distant stars.")]
        [SerializeField] float farDensity = 0.42f;
        [Tooltip("Ship-travel slide. Keep tiny — this layer is “infinity.” Nebula scroll is ~0.01.")]
        [SerializeField] float farParallax = 0.0015f;
        [Tooltip("Star radius as a fraction of one cell.")]
        [SerializeField] float farSize = 0.028f;
        [Tooltip("Per-star brightness for the distant layer.")]
        [SerializeField] float farBrightness = 0.4f;
        [Tooltip("Chance a cell actually draws a star (avoids a regular grid).")]
        [SerializeField] [Range(0f, 1f)] float farOccupancy = 0.55f;

        [Header("Mid layer")]
        [Tooltip("Cells per world unit for the middle parallax layer.")]
        [SerializeField] float midDensity = 0.22f;
        [Tooltip("Still far behind the nebula. Only a hint of drift vs the far layer.")]
        [SerializeField] float midParallax = 0.0035f;
        [Tooltip("Star radius as a fraction of one cell.")]
        [SerializeField] float midSize = 0.038f;
        [Tooltip("Per-star brightness for the middle layer.")]
        [SerializeField] float midBrightness = 0.7f;
        [Tooltip("Chance a mid-layer cell draws a star.")]
        [SerializeField] [Range(0f, 1f)] float midOccupancy = 0.38f;

        [Header("Near layer (least-far of the three; still deep space)")]
        [Tooltip("Cells per world unit. Lower = sparser close stars.")]
        [SerializeField] float nearDensity = 0.11f;
        [Tooltip("Fastest of the three, but still slower than the nebula (~0.01) so it reads as far back.")]
        [SerializeField] float nearParallax = 0.007f;
        [Tooltip("Star radius as a fraction of one cell.")]
        [SerializeField] float nearSize = 0.05f;
        [Tooltip("Per-star brightness for the near layer.")]
        [SerializeField] float nearBrightness = 0.95f;
        [Tooltip("Chance a near-layer cell draws a star.")]
        [SerializeField] [Range(0f, 1f)] float nearOccupancy = 0.28f;

        /// <summary>Runtime quad renderer. Hidden when the GameManager toggle is off; never destroyed for a cheap re-enable.</summary>
        MeshRenderer meshRenderer;

        /// <summary>Instance material so we can SetVector without MaterialPropertyBlock (WebGL / GLES safe).</summary>
        Material starMaterial;

        /// <summary>Quad transform we resize to cover the camera view.</summary>
        Transform starQuadTransform;

        /// <summary>True after the first LateUpdate sample so we can compute a travel delta.</summary>
        bool hasLastScrollPos;

        /// <summary>Previous follow position used to accumulate wrap-safe travel.</summary>
        Vector3 lastScrollPos;

        /// <summary>Accumulated ship travel on XZ (not raw world position). Shader multiplies this by each layer’s parallax.</summary>
        float scrollOffsetX;

        /// <summary>Accumulated ship travel on Z. Paired with <see cref="scrollOffsetX"/>.</summary>
        float scrollOffsetZ;

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        static readonly int TwinkleId = Shader.PropertyToID("_Twinkle");
        static readonly int FollowXZId = Shader.PropertyToID("_FollowXZ");
        static readonly int QuadScaleId = Shader.PropertyToID("_QuadScale");
        static readonly int OccupancyId = Shader.PropertyToID("_Occupancy");
        static readonly int LayerFarId = Shader.PropertyToID("_LayerFar");
        static readonly int LayerMidId = Shader.PropertyToID("_LayerMid");
        static readonly int LayerNearId = Shader.PropertyToID("_LayerNear");

        /// <summary>
        /// [UNITY] Awake — subscribe to GameManager, then either bail on a headless server or
        /// build the star quad. Unsubscribe only in OnDestroy so <c>enabled = false</c> does
        /// not drop the listener we need to turn the field back on.
        /// </summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            GameManager.ShowStarfieldBackgroundChanged += OnShowStarfieldBackgroundChanged;

            // Headless dedicated (Edgegap/GCE): no shaders in the server player.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                enabled = false;
                return;
            }

            ResolveTargetCamera();
            if (targetCamera == null)
            {
                Debug.LogWarning("ParallaxStarfieldBackground: No camera assigned and Main Camera not found.");
                return;
            }

            EnsureStarQuad();
        }

        /// <summary>
        /// [UNITY] Start — sync with GameManager if its Awake fired before we subscribed.
        /// </summary>
        void Start()
        {
            ApplyFeatureEnabled(GameManager.IsShowStarfieldBackgroundActive);
        }

        /// <summary>
        /// [UNITY] OnEnable — rebuild the quad if a toggle or parent disabled us after first setup.
        /// </summary>
        void OnEnable()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (targetCamera == null)
                ResolveTargetCamera();
            if (meshRenderer == null && targetCamera != null)
                EnsureStarQuad();
            if (meshRenderer != null && GameManager.IsShowStarfieldBackgroundActive)
                meshRenderer.enabled = true;
        }

        /// <summary>
        /// [UNITY] OnDisable — hide the quad so a disabled component never leaves a leftover draw.
        /// </summary>
        void OnDisable()
        {
            if (meshRenderer != null)
                meshRenderer.enabled = false;
        }

        /// <summary>
        /// [UNITY] OnDestroy — drop the static event and destroy the instance material.
        /// </summary>
        void OnDestroy()
        {
            GameManager.ShowStarfieldBackgroundChanged -= OnShowStarfieldBackgroundChanged;
            if (starMaterial != null)
                Object.Destroy(starMaterial);
        }

        /// <summary>
        /// GameManager published a new Show Starfield Background value (Play Mode Inspector or Awake).
        /// </summary>
        /// <param name="show">True when the starfield should draw and LateUpdate.</param>
        void OnShowStarfieldBackgroundChanged(bool show) => ApplyFeatureEnabled(show);

        /// <summary>
        /// Turns the starfield fully on or off. Off hides the renderer and sets
        /// <c>enabled = false</c> so Unity skips LateUpdate — not merely a black quad that still ticks.
        /// </summary>
        /// <param name="show">GameManager toggle value.</param>
        void ApplyFeatureEnabled(bool show)
        {
            // Headless already bailed in Awake and must never be re-enabled by a leftover Inspector tick.
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

            // Re-enable after a toggle-off: treat the next pose as a fresh sample so we do not
            // add a huge delta from the frames we skipped.
            hasLastScrollPos = false;

            if (meshRenderer == null)
                EnsureStarQuad();
            if (meshRenderer != null)
                meshRenderer.enabled = true;
        }

        /// <summary>
        /// [UNITY] LateUpdate — after CameraFollowEcs (67001) and the nebula (67100). Repositions
        /// the quad under the ship, accumulates wrap-safe travel, and writes shader uniforms.
        /// No allocations; no Find calls after the first resolve.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            ResolveTargetCamera();
            if (targetCamera == null)
                return;

            if (meshRenderer == null || starMaterial == null)
                EnsureStarQuad();
            if (starMaterial == null)
                return;

            // [HYBRID] Presentation pose, not raw sim — same source the camera and nebula use.
            Vector3 followPos;
            if (ShipDisplayPose.HasLocalPose)
                followPos = ShipDisplayPose.LocalPosition;
            else
                followPos = targetCamera.transform.position;

            transform.position = new Vector3(followPos.x, -Mathf.Abs(depthOffset), followPos.z);
            float quadSize = ResizeQuadToCoverView();

            AccumulateWrapSafeScroll(followPos);
            WriteShaderUniforms(quadSize);
        }

        /// <summary>
        /// Adds this frame’s ship travel to the parallax scroll. Uses the torus shortest offset
        /// when map size is known so a wrap snap does not yank the stars. If map size is not
        /// ready yet, a huge one-frame jump is treated as a snap and skipped.
        /// </summary>
        /// <param name="followPos">Ship display position (or camera fallback) this frame.</param>
        void AccumulateWrapSafeScroll(Vector3 followPos)
        {
            if (!hasLastScrollPos)
            {
                lastScrollPos = followPos;
                hasLastScrollPos = true;
                return;
            }

            float dx = followPos.x - lastScrollPos.x;
            float dz = followPos.z - lastScrollPos.z;
            lastScrollPos = followPos;

            ResolveWrapSafeDelta(ref dx, ref dz);
            scrollOffsetX += dx;
            scrollOffsetZ += dz;
        }

        /// <summary>
        /// Rewrites <paramref name="dx"/> / <paramref name="dz"/> to the shortest XZ step on the
        /// torus, or zeroes a huge jump when map size is still unknown.
        /// </summary>
        static void ResolveWrapSafeDelta(ref float dx, ref float dz)
        {
            // [TITAN-ORBIT] Map width/height come from the server recipe cache — never invent 1000×1000.
            if (MapSessionMetaCache.HasMapSize)
            {
                float mapW = MapSessionMetaCache.MapWidth;
                float mapH = MapSessionMetaCache.MapHeight;
                float halfW = mapW * 0.5f;
                float halfH = mapH * 0.5f;
                if (dx > halfW)
                    dx -= mapW;
                else if (dx < -halfW)
                    dx += mapW;
                if (dz > halfH)
                    dz -= mapH;
                else if (dz < -halfH)
                    dz += mapH;
                return;
            }

            if (dx * dx + dz * dz > WrapSnapFallbackThreshold * WrapSnapFallbackThreshold)
            {
                dx = 0f;
                dz = 0f;
            }
        }

        /// <summary>
        /// Pushes look + layer + follow uniforms onto the instance material. One SetVector
        /// family per frame — no MaterialPropertyBlock (GLES / WebGL + SRP Batcher issue).
        /// </summary>
        /// <param name="quadSize">World-space quad edge length from <see cref="ResizeQuadToCoverView"/>.</param>
        void WriteShaderUniforms(float quadSize)
        {
            starMaterial.SetColor(TintId, tint);
            starMaterial.SetFloat(BrightnessId, brightness);
            starMaterial.SetFloat(TwinkleId, twinkle);
            starMaterial.SetVector(FollowXZId, new Vector4(scrollOffsetX, scrollOffsetZ, 0f, 0f));
            starMaterial.SetVector(QuadScaleId, new Vector4(quadSize, quadSize, 0f, 0f));
            starMaterial.SetVector(OccupancyId, new Vector4(farOccupancy, midOccupancy, nearOccupancy, 0f));
            starMaterial.SetVector(LayerFarId, new Vector4(farDensity, farParallax, farSize, farBrightness));
            starMaterial.SetVector(LayerMidId, new Vector4(midDensity, midParallax, midSize, midBrightness));
            starMaterial.SetVector(LayerNearId, new Vector4(nearDensity, nearParallax, nearSize, nearBrightness));
        }

        /// <summary>
        /// Caches Main Camera once so LateUpdate never calls <c>Camera.main</c> after the first miss.
        /// </summary>
        void ResolveTargetCamera()
        {
            if (targetCamera != null)
                return;
            targetCamera = UnityEngine.Camera.main;
        }

        /// <summary>
        /// Creates the follow quad and instance material the first time we need to draw.
        /// No collider, no shadows. Render queue 1001 (Background+1) so all three star layers
        /// stamp after the nebula and before every gameplay opaque (ships, planets, asteroids).
        /// </summary>
        void EnsureStarQuad()
        {
            if (meshRenderer != null)
                return;

            Material template = Resources.Load<Material>(StarfieldMaterialResourcePath);
            if (template != null)
                starMaterial = new Material(template);
            else
            {
                Shader shader = Shader.Find(StarfieldShaderName);
                if (shader == null)
                {
                    Debug.LogError(
                        "ParallaxStarfieldBackground: Missing Resources/" + StarfieldMaterialResourcePath +
                        " and shader \"" + StarfieldShaderName + "\" was not found.");
                    return;
                }

                starMaterial = new Material(shader);
            }

            // [UNITY] Background+1 — must stay below 2500 or URP sorts this giant quad with
            // transparents and the stars draw on top of hulls. Opaque tag blocks that path.
            starMaterial.SetOverrideTag("RenderType", "Opaque");
            starMaterial.SetFloat("_Surface", 0f);
            starMaterial.renderQueue = 1001;

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "StarfieldBackgroundQuad";
            quad.transform.SetParent(transform);
            starQuadTransform = quad.transform;
            starQuadTransform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ResizeQuadToCoverView();

            Object.Destroy(quad.GetComponent<Collider>());

            meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = starMaterial;
            meshRenderer.SetPropertyBlock(null);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = GameManager.IsShowStarfieldBackgroundActive;
        }

        /// <summary>
        /// Sizes the XZ quad so it covers the camera frustum at the starfield plane, plus
        /// <see cref="sizeMargin"/>. Same math as the nebula so both stay edge-safe.
        /// </summary>
        /// <returns>World-space edge length written to the quad (0 if we cannot size yet).</returns>
        float ResizeQuadToCoverView()
        {
            if (targetCamera == null || starQuadTransform == null)
                return 0f;

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
            starQuadTransform.localScale = new Vector3(quadSize, quadSize, 1f);
            return quadSize;
        }
    }
}
