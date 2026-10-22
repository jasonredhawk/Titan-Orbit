using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Second sun shadow for the local ship only. The directional light keeps its camera cascades
    /// for the rest of the scene. This map is a 1024 orthographic fit, about two units wide,
    /// snapped to the hull along the same sun direction so a quarter-unit ship is not stuck
    /// in a cascade texel the size of its hull.
    /// </summary>
    public sealed class ShipFittedShadowFeature : ScriptableRendererFeature
    {
        const string ShaderName = "Hidden/TitanOrbit/ShipFittedShadowApply";
        internal const int MapResolution = 1024;
        internal const float MapBaseExtent = 2f;
        internal const float ShadowedFloor = 0.45f;

        static readonly int WorldToShadowId = Shader.PropertyToID("_ShipFittedWorldToShadow");
        static readonly int ParamsId = Shader.PropertyToID("_ShipFittedParams");
        static readonly int ToLightId = Shader.PropertyToID("_ShipFittedToLight");
        static readonly int ShadowMapId = Shader.PropertyToID("_ShipFittedShadowMap");
        static readonly int LightDirectionId = Shader.PropertyToID("_LightDirection");
        static readonly int ShadowBiasId = Shader.PropertyToID("_ShadowBias");

        /// <summary>
        /// True once the apply shader is loaded. The caster driver leaves cascade casting alone until then,
        /// so a missing shader cannot strip the ship's ordinary shadow.
        /// </summary>
        internal static bool Ready { get; private set; }

        [SerializeField] Shader applyShader;

        Material _material;
        ShipFittedShadowMapPass _mapPass;
        ShipFittedShadowApplyPass _applyPass;
        internal TextureHandle PublishedShadow;

        public void AssignApplyShader(Shader shader) => applyShader = shader;

        public override void Create()
        {
            _mapPass = new ShipFittedShadowMapPass();
            _applyPass = new ShipFittedShadowApplyPass();
            _mapPass.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            _applyPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            Ready = ResolveShader() != null;
        }

        protected override void Dispose(bool disposing)
        {
            Ready = false;
            CoreUtils.Destroy(_material);
            _material = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            PublishedShadow = TextureHandle.nullHandle;

            var cameraData = renderingData.cameraData;
            if (cameraData.cameraType != CameraType.Game || cameraData.renderType != CameraRenderType.Base)
                return;
            if (cameraData.camera == null || cameraData.camera != Camera.main)
                return;

            if (!TryGetMaterial(out Material material))
            {
                Ready = false;
                return;
            }

            Ready = true;
            RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
            if (!ShipFittedShadowCasters.TryBuildFrame(cameraData.camera, target.width, target.height, out ShipFittedShadowFrame frame))
                return;

            _mapPass.Setup(frame, this);
            _applyPass.Setup(frame, material, this);
            renderer.EnqueuePass(_mapPass);
            renderer.EnqueuePass(_applyPass);
        }

        bool TryGetMaterial(out Material material)
        {
            material = _material;
            if (material != null)
                return true;

            Shader shader = ResolveShader();
            if (shader == null)
                return false;

            _material = CoreUtils.CreateEngineMaterial(shader);
            material = _material;
            return material != null;
        }

        Shader ResolveShader()
        {
            if (applyShader != null)
                return applyShader;
            return Shader.Find(ShaderName);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Boot()
        {
#if UNITY_SERVER
            return;
#else
            InstallOnActivePipeline();
            if (Object.FindAnyObjectByType<ShipFittedShadowDriver>() != null)
                return;

            var go = new GameObject(nameof(ShipFittedShadowDriver))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            go.AddComponent<ShipFittedShadowDriver>();
            Object.DontDestroyOnLoad(go);
#endif
        }

        static void InstallOnActivePipeline()
        {
            Install(UniversalRenderPipeline.asset);
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset fallback)
                Install(fallback);
            if (QualitySettings.renderPipeline is UniversalRenderPipelineAsset qualityAsset)
                Install(qualityAsset);
        }

        static void Install(UniversalRenderPipelineAsset asset)
        {
            if (asset == null)
                return;

            var list = asset.rendererDataList;
            for (int i = 0; i < list.Length; i++)
            {
                ScriptableRendererData data = list[i];
                if (data == null || data.TryGetRendererFeature<ShipFittedShadowFeature>(out _))
                    continue;

                var feature = CreateInstance<ShipFittedShadowFeature>();
                feature.name = "Ship Fitted Shadow";
                feature.hideFlags = HideFlags.HideAndDontSave;
                data.rendererFeatures.Add(feature);
                data.SetDirty();
            }
        }

        sealed class ShipFittedShadowMapPass : ScriptableRenderPass
        {
            ShipFittedShadowFrame _frame;
            ShipFittedShadowFeature _owner;
            readonly List<DrawCall> _draws = new List<DrawCall>(32);

            public ShipFittedShadowMapPass()
            {
                profilingSampler = new ProfilingSampler("Ship Fitted Shadow Map");
            }

            public void Setup(in ShipFittedShadowFrame frame, ShipFittedShadowFeature owner)
            {
                _frame = frame;
                _owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                _draws.Clear();
                ShipFittedShadowCasters.CollectDraws(_draws);
                if (_draws.Count == 0)
                    return;

                var desc = new TextureDesc(ShipFittedShadowFeature.MapResolution, ShipFittedShadowFeature.MapResolution)
                {
                    name = "_ShipFittedShadowMap",
                    depthBufferBits = DepthBits.Depth16,
                    isShadowMap = true,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    clearBuffer = true,
                    clearColor = Color.black,
                    disableFallBackToImportedTexture = true
                };

                TextureHandle shadow = renderGraph.CreateTexture(desc);
                if (_owner != null)
                    _owner.PublishedShadow = shadow;

                using (var builder = renderGraph.AddUnsafePass<MapPassData>("Ship Fitted Shadow Map", out var passData, profilingSampler))
                {
                    passData.Shadow = shadow;
                    passData.View = _frame.View;
                    passData.GpuProj = _frame.GpuProjection;
                    passData.CameraView = cameraData.GetViewMatrix();
                    passData.CameraGpuProj = cameraData.GetProjectionMatrix();
                    passData.LightDirection = _frame.ToLight;
                    passData.ShadowBias = _frame.CasterBias;
                    passData.Draws = _draws;

                    builder.UseTexture(shadow, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetGlobalTextureAfterPass(shadow, ShadowMapId);
                    builder.SetRenderFunc(static (MapPassData data, UnsafeGraphContext context) =>
                    {
                        Execute(context.cmd, data);
                    });
                }
            }

            static void Execute(UnsafeCommandBuffer cmd, MapPassData data)
            {
                float farClear = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                cmd.SetRenderTarget(data.Shadow, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                cmd.SetViewport(new Rect(0f, 0f, ShipFittedShadowFeature.MapResolution, ShipFittedShadowFeature.MapResolution));
                cmd.ClearRenderTarget(true, false, Color.black, farClear);
                cmd.SetGlobalVector(LightDirectionId, data.LightDirection);
                cmd.SetGlobalVector(ShadowBiasId, data.ShadowBias);
                cmd.SetViewProjectionMatrices(data.View, data.GpuProj);

                var draws = data.Draws;
                for (int i = 0; i < draws.Count; i++)
                {
                    DrawCall draw = draws[i];
                    if (draw.Renderer == null || draw.Material == null)
                        continue;
                    cmd.DrawRenderer(draw.Renderer, draw.Material, draw.Submesh, draw.Pass);
                }

                cmd.SetViewProjectionMatrices(data.CameraView, data.CameraGpuProj);
            }

            sealed class MapPassData
            {
                public TextureHandle Shadow;
                public Matrix4x4 View;
                public Matrix4x4 GpuProj;
                public Matrix4x4 CameraView;
                public Matrix4x4 CameraGpuProj;
                public Vector4 LightDirection;
                public Vector4 ShadowBias;
                public List<DrawCall> Draws;
            }
        }

        sealed class ShipFittedShadowApplyPass : ScriptableRenderPass
        {
            ShipFittedShadowFrame _frame;
            Material _material;
            ShipFittedShadowFeature _owner;
            Rect _scissor;
            bool _scissorFull;

            public ShipFittedShadowApplyPass()
            {
                profilingSampler = new ProfilingSampler("Ship Fitted Shadow Apply");
            }

            public void Setup(in ShipFittedShadowFrame frame, Material material, ShipFittedShadowFeature owner)
            {
                _frame = frame;
                _material = material;
                _owner = owner;
                _scissor = frame.ScissorPixels;
                _scissorFull = frame.ScissorCoversTarget;
                ConfigureInput(ScriptableRenderPassInput.Depth);
                requiresIntermediateTexture = false;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null || _owner == null || !_owner.PublishedShadow.IsValid())
                    return;
                if (_scissor.width < 1f || _scissor.height < 1f)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!resources.activeColorTexture.IsValid() || !resources.cameraDepthTexture.IsValid())
                    return;

                using (var builder = renderGraph.AddRasterRenderPass<ApplyPassData>("Ship Fitted Shadow Apply", out var passData, profilingSampler))
                {
                    passData.Material = _material;
                    passData.Shadow = _owner.PublishedShadow;
                    passData.WorldToShadow = _frame.WorldToShadow;
                    passData.Params = _frame.ShaderParams;
                    passData.ToLight = _frame.ToLight;
                    passData.Scissor = _scissor;
                    passData.UseScissor = !_scissorFull;

                    builder.UseTexture(_owner.PublishedShadow, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (ApplyPassData data, RasterGraphContext context) =>
                    {
                        Execute(context.cmd, data);
                    });
                }
            }

            static void Execute(RasterCommandBuffer cmd, ApplyPassData data)
            {
                cmd.SetGlobalTexture(ShadowMapId, data.Shadow);
                cmd.SetGlobalMatrix(WorldToShadowId, data.WorldToShadow);
                cmd.SetGlobalVector(ParamsId, data.Params);
                cmd.SetGlobalVector(ToLightId, data.ToLight);
                if (data.UseScissor)
                    cmd.EnableScissorRect(data.Scissor);

                cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1);

                if (data.UseScissor)
                    cmd.DisableScissorRect();
            }

            sealed class ApplyPassData
            {
                public Material Material;
                public TextureHandle Shadow;
                public Matrix4x4 WorldToShadow;
                public Vector4 Params;
                public Vector4 ToLight;
                public Rect Scissor;
                public bool UseScissor;
            }
        }
    }

    /// <summary>One frame of fitted-shadow constants, built on the main thread before the passes record.</summary>
    struct ShipFittedShadowFrame
    {
        public Matrix4x4 View;
        public Matrix4x4 GpuProjection;
        public Matrix4x4 WorldToShadow;
        public Vector4 ToLight;
        public Vector4 CasterBias;
        public Vector4 ShaderParams;
        public Rect ScissorPixels;
        public bool ScissorCoversTarget;
    }

    struct DrawCall
    {
        public Renderer Renderer;
        public Material Material;
        public int Submesh;
        public int Pass;
    }

    /// <summary>
    /// Holds the local hull out of the camera cascades and publishes the renderers the fitted map draws.
    /// Runs after <see cref="EcsWorldVisualizer"/> so the proxy pose for this frame is already applied.
    /// </summary>
    static class ShipFittedShadowCasters
    {
        static readonly List<Renderer> Scratch = new List<Renderer>(64);
        static readonly List<Material> Materials = new List<Material>(8);
        static readonly Dictionary<int, int> ShadowPassByMaterial = new Dictionary<int, int>(32);
        static readonly Dictionary<Renderer, ShadowCastingMode> Suppressed = new Dictionary<Renderer, ShadowCastingMode>(32);
        static readonly List<Renderer> RemoveBuffer = new List<Renderer>(16);
        static Light _sun;
        static int _sunSearchFrame = -1000;

        public static bool TryBuildFrame(Camera camera, int targetWidth, int targetHeight, out ShipFittedShadowFrame frame)
        {
            frame = default;
            if (camera == null || targetWidth < 1 || targetHeight < 1)
                return false;
            if (!TryGetFocus(out Bounds bounds))
                return false;

            Light sun = ResolveSun();
            if (sun == null)
                return false;

            Vector3 ray = sun.transform.forward;
            if (ray.sqrMagnitude < 1e-6f)
                return false;
            ray.Normalize();

            Vector3 up = sun.transform.up;
            if (Mathf.Abs(Vector3.Dot(up, ray)) > 0.99f)
                up = Mathf.Abs(ray.y) < 0.9f ? Vector3.up : Vector3.right;

            float hull = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float extent = Mathf.Max(ShipFittedShadowFeature.MapBaseExtent, hull * 1.75f);
            float depthRange = Mathf.Max(4f, hull * 4f);
            float texel = extent / ShipFittedShadowFeature.MapResolution;

            Vector3 axisX = Vector3.Cross(up, ray).normalized;
            Vector3 axisY = Vector3.Cross(ray, axisX).normalized;
            Vector3 snapped = Snap(bounds.center, axisX, axisY, ray, texel);
            Vector3 eye = snapped - ray * (depthRange * 0.5f);
            Quaternion rotation = Quaternion.LookRotation(ray, axisY);

            // Match Camera.worldToCameraMatrix: Unity view space looks down -Z in the unflipped local matrix.
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                             Matrix4x4.TRS(eye, rotation, Vector3.one).inverse;
            float half = extent * 0.5f;
            Matrix4x4 proj = Matrix4x4.Ortho(-half, half, -half, half, 0.05f, depthRange);
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, true);
            Matrix4x4 clipToUv = Matrix4x4.TRS(new Vector3(0.5f, 0.5f, 0.5f), Quaternion.identity, new Vector3(0.5f, 0.5f, 0.5f));

            Rect scissor = ProjectScissor(
                camera,
                targetWidth,
                targetHeight,
                snapped,
                axisX,
                axisY,
                ray,
                half,
                depthRange * 0.5f,
                out bool coversTarget);

            if (scissor.width < 1f || scissor.height < 1f)
                return false;

            // URP applies bias as a negative world offset. Scale it by this map's texel, not the cascade texel.
            Vector4 casterBias = new Vector4(-texel * 2f, -texel * 1f, 0f, 0f);
            Vector4 toLight = -ray;
            frame = new ShipFittedShadowFrame
            {
                View = view,
                GpuProjection = gpuProj,
                WorldToShadow = clipToUv * gpuProj * view,
                ToLight = toLight,
                CasterBias = casterBias,
                ShaderParams = new Vector4(
                    1f / ShipFittedShadowFeature.MapResolution,
                    sun.shadowStrength,
                    texel * 0.75f / Mathf.Max(depthRange, 0.01f),
                    ShipFittedShadowFeature.ShadowedFloor),
                ScissorPixels = scissor,
                ScissorCoversTarget = coversTarget
            };
            return true;
        }

        public static void CollectDraws(List<DrawCall> draws)
        {
            foreach (var pair in Suppressed)
            {
                Renderer renderer = pair.Key;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                int submeshes = SubmeshCount(renderer);
                renderer.GetSharedMaterials(Materials);
                if (Materials.Count == 0)
                    continue;

                for (int s = 0; s < submeshes; s++)
                {
                    Material material = Materials[Mathf.Min(s, Materials.Count - 1)];
                    int pass = FindShadowPass(material);
                    if (pass < 0)
                        continue;
                    draws.Add(new DrawCall
                    {
                        Renderer = renderer,
                        Material = material,
                        Submesh = s,
                        Pass = pass
                    });
                }
            }
        }

        public static void Sync(Transform root)
        {
            if (!ShipFittedShadowFeature.Ready || root == null)
            {
                RestoreAll();
                return;
            }

            root.GetComponentsInChildren(false, Scratch);
            for (int i = 0; i < Scratch.Count; i++)
            {
                Renderer renderer = Scratch[i];
                if (renderer == null)
                    continue;
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                    continue;
                if (Suppressed.ContainsKey(renderer))
                    continue;

                ShadowCastingMode mode = renderer.shadowCastingMode;
                if (mode == ShadowCastingMode.Off)
                    continue;
                if (!HasShadowPass(renderer))
                    continue;
                if (renderer.bounds.extents.sqrMagnitude > 100f)
                    continue;

                Suppressed.Add(renderer, mode);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            RemoveBuffer.Clear();
            foreach (var pair in Suppressed)
            {
                Renderer renderer = pair.Key;
                if (renderer == null || (renderer.transform != root && !renderer.transform.IsChildOf(root)))
                    RemoveBuffer.Add(renderer);
            }

            for (int i = 0; i < RemoveBuffer.Count; i++)
            {
                Renderer renderer = RemoveBuffer[i];
                if (renderer != null && Suppressed.TryGetValue(renderer, out ShadowCastingMode mode))
                    renderer.shadowCastingMode = mode;
                Suppressed.Remove(renderer);
            }
        }

        static void RestoreAll()
        {
            if (Suppressed.Count == 0)
                return;

            foreach (var pair in Suppressed)
            {
                if (pair.Key != null)
                    pair.Key.shadowCastingMode = pair.Value;
            }

            Suppressed.Clear();
        }

        static bool TryGetFocus(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var pair in Suppressed)
            {
                Renderer renderer = pair.Key;
                if (renderer == null || !renderer.enabled)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }

        static bool HasShadowPass(Renderer renderer)
        {
            renderer.GetSharedMaterials(Materials);
            for (int i = 0; i < Materials.Count; i++)
            {
                if (FindShadowPass(Materials[i]) >= 0)
                    return true;
            }

            return false;
        }

        static int FindShadowPass(Material material)
        {
            if (material == null || material.shader == null)
                return -1;

            int id = material.GetInstanceID();
            if (ShadowPassByMaterial.TryGetValue(id, out int pass))
                return pass;

            if (ShadowPassByMaterial.Count > 128)
                ShadowPassByMaterial.Clear();

            pass = material.FindPass("ShadowCaster");
            ShadowPassByMaterial[id] = pass;
            return pass;
        }

        static int SubmeshCount(Renderer renderer)
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
                mesh = skinned.sharedMesh;
            else if (renderer.TryGetComponent(out MeshFilter filter))
                mesh = filter.sharedMesh;
            return mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1;
        }

        static Light ResolveSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled && _sun.type == LightType.Directional && _sun.shadows != LightShadows.None)
                return _sun;

            if (Time.frameCount - _sunSearchFrame < 60)
                return null;

            _sunSearchFrame = Time.frameCount;
            _sun = RenderSettings.sun;
            if (_sun != null && _sun.type == LightType.Directional && _sun.shadows != LightShadows.None)
                return _sun;

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light.type == LightType.Directional && light.shadows != LightShadows.None)
                {
                    _sun = light;
                    return _sun;
                }
            }

            _sun = null;
            return null;
        }

        static Vector3 Snap(Vector3 point, Vector3 axisX, Vector3 axisY, Vector3 axisZ, float texel)
        {
            float x = Mathf.Round(Vector3.Dot(point, axisX) / texel) * texel;
            float y = Mathf.Round(Vector3.Dot(point, axisY) / texel) * texel;
            float z = Mathf.Round(Vector3.Dot(point, axisZ) / texel) * texel;
            return axisX * x + axisY * y + axisZ * z;
        }

        static Rect ProjectScissor(
            Camera camera,
            int width,
            int height,
            Vector3 center,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            float halfExtent,
            float halfDepth,
            out bool coversTarget)
        {
            coversTarget = false;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            bool anyVisible = false;
            bool anyBehind = false;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center
                    + axisX * (((i & 1) == 0) ? -halfExtent : halfExtent)
                    + axisY * (((i & 2) == 0) ? -halfExtent : halfExtent)
                    + axisZ * (((i & 4) == 0) ? -halfDepth : halfDepth);
                Vector3 viewport = camera.WorldToViewportPoint(corner);
                if (viewport.z <= 0f)
                {
                    anyBehind = true;
                    continue;
                }

                anyVisible = true;
                minX = Mathf.Min(minX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxX = Mathf.Max(maxX, viewport.x);
                maxY = Mathf.Max(maxY, viewport.y);
            }

            if (!anyVisible)
                return new Rect(0f, 0f, 0f, 0f);
            if (anyBehind)
            {
                coversTarget = true;
                return new Rect(0f, 0f, width, height);
            }

            const float pad = 0.03f;
            minX = Mathf.Clamp01(minX - pad) * width;
            minY = Mathf.Clamp01(minY - pad) * height;
            maxX = Mathf.Clamp01(maxX + pad) * width;
            maxY = Mathf.Clamp01(maxY + pad) * height;
            if (maxX - minX >= width - 1f && maxY - minY >= height - 1f)
                coversTarget = true;
            return new Rect(minX, minY, Mathf.Max(0f, maxX - minX), Mathf.Max(0f, maxY - minY));
        }
    }

    [DefaultExecutionOrder(67000)]
    sealed class ShipFittedShadowDriver : MonoBehaviour
    {
        void LateUpdate()
        {
            ShipFittedShadowCasters.Sync(EcsWorldVisualizer.LocalPlayerShipVisualRoot);
        }
    }
}
