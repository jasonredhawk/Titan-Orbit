using System.Collections.Generic;
using SpaceGraphicsToolkit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.NetCode;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Join-load worker that compiles URP shader variants and pays first-use Instantiates
    /// <b>under the loading overlay</b> so spawn does not 60↔30 VSync-bounce.
    /// <para>
    /// [UNITY] Creating a Material or Instantiates a prefab does <b>not</b> compile shader
    /// variants — a real draw does. This worker parks a hidden camera at (0, −10000, 0) and
    /// calls <c>Camera.Render()</c> on a quad (and a starting hull / one VFX shell) so the
    /// gameplay camera is not the first draw. Budgeted: 4 material draws and 4 VFX Instantiates
    /// per frame. Never renders the live map.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] VSync stays on (<c>vSyncCount = 1</c>) and client <c>MaxSteps</c> stays 8.
    /// Those were already tried and rejected (tear bands / reconcile snaps). This path only
    /// moves cold GPU + Instantiates cost earlier. Dedicated servers skip entirely.
    /// </para>
    /// Ticked from <see cref="PresentationJoinWarmupGate.Tick"/>. One tick per Unity frame
    /// even when LoadingScreen and NceGameFlowController both call the gate.
    /// </summary>
    public static class PresentationJoinWarmup
    {
        /// <summary>Seconds after the first tick before Join Team is released anyway.</summary>
        const float TimeoutSeconds = 15f;

        /// <summary>Max unique materials drawn with <c>Camera.Render</c> this frame.</summary>
        const int MaterialsPerFrame = 4;

        /// <summary>Max new proxy GameObjects scanned for materials this frame.</summary>
        const int ProxyScansPerFrame = 16;

        /// <summary>Max VFX prefabs whose renderer materials we enqueue this frame.</summary>
        const int VfxPrefabsCollectedPerFrame = 8;

        /// <summary>Same Instantiates budget <see cref="BulletVfxDriver"/> uses in LateUpdate.</summary>
        const int VfxInstantiatesPerFrame = 4;

        /// <summary>
        /// Max dirty SGT planet Rebuilds this frame. Profiler: one first Rebuild was ~845 ms —
        /// keep this at 1 so the overlay absorbs it without stacking.
        /// </summary>
        const int SgtPlanetRebuildsPerFrame = 1;

        /// <summary>Small off-screen target so URP does not blit into the Game view overlay.</summary>
        const int WarmupRtSize = 64;

        /// <summary>
        /// Unused built-in-adjacent layer so the warmup camera can isolate the quad / hull.
        /// Main cameras still use Everything — objects sit at y = −10000, outside the frustum.
        /// </summary>
        const int WarmupLayer = 31;

        /// <summary>World-space park for the hidden camera, quad, and hull probe.</summary>
        static readonly Vector3 WarmupOrigin = new Vector3(0f, -10000f, 0f);

        /// <summary><see cref="Time.frameCount"/> of the last worker tick (dedupe dual callers).</summary>
        static int s_LastTickFrame = -1;

        /// <summary>Realtime when warmup first ran this session. −1 = not started.</summary>
        static float s_StartedRealtime = -1f;

        /// <summary>True after timeout / successful finish — do not keep Instantiates.</summary>
        static bool s_Finished;

        /// <summary>Hidden camera that we <c>Render()</c> manually (never enabled on the player loop).</summary>
        static Camera s_Camera;

        /// <summary>
        /// Tiny RenderTexture the warmup camera draws into.
        /// [UNITY] URP <c>Camera.Render()</c> without a target blits to the Game view and
        /// fights the overlay (console: BlitFinalToBackBuffer attachment size mismatch).
        /// </summary>
        static RenderTexture s_WarmupRt;

        /// <summary>Single quad whose <c>sharedMaterial</c> we swap to force each variant.</summary>
        static Renderer s_QuadRenderer;

        /// <summary>Root of the hidden camera + quad (destroyed on reset / complete).</summary>
        static GameObject s_RigRoot;

        /// <summary>Starting-family hull Instantiates for first-draw of ship shaders.</summary>
        static GameObject s_HullProbe;

        /// <summary>Family used for <see cref="ShipVisualApplier.ApplyTeamMaterials"/> on the probe.</summary>
        static ShipFamilyDefinition s_HullFamily;

        /// <summary>Next team to paint on the hull (TeamA after create, then B–E).</summary>
        static TeamId s_NextHullTeam = TeamId.TeamA;

        /// <summary>True when the hull cycle finished or was skipped (missing family).</summary>
        static bool s_HullDone;

        /// <summary>True after we asked the VFX pool to enqueue bank prefabs.</summary>
        static bool s_VfxEnqueued;

        /// <summary>True after gem shared tints were offered to the material queue.</summary>
        static bool s_GemTintsEnqueued;

        /// <summary>Cursor through bank category × team × prefab-kind while collecting VFX materials.</summary>
        static int s_VfxCollectCursor;

        /// <summary>Unique materials waiting for a <c>Camera.Render</c>.</summary>
        static readonly List<Material> s_MaterialQueue = new List<Material>(128);

        /// <summary>Material InstanceIDs already queued or drawn this session.</summary>
        static readonly HashSet<int> s_SeenMaterialIds = new HashSet<int>();

        /// <summary>Proxy entities whose renderers we already scanned.</summary>
        static readonly HashSet<Entity> s_ScannedProxyEntities = new HashSet<Entity>();

        /// <summary>Unique VFX prefabs we will Rent once for a particle-shader draw.</summary>
        static readonly List<GameObject> s_VfxShellPrefabs = new List<GameObject>(64);

        /// <summary>Prefab InstanceIDs already on <see cref="s_VfxShellPrefabs"/>.</summary>
        static readonly HashSet<int> s_SeenVfxPrefabIds = new HashSet<int>();

        /// <summary>How many VFX shells we have Camera.Render'd this session.</summary>
        static int s_VfxShellsRendered;

        /// <summary>How many queued materials have been drawn.</summary>
        static int s_MaterialsRendered;

        /// <summary>
        /// True after map proxies hit the Join Team ready ratio and we finished one full
        /// scan of the live set. Distance-importance Instantiates after that must not
        /// keep the gate open forever.
        /// </summary>
        static bool s_ProxyScanClosed;

        /// <summary>True after every live planet proxy's SgtPlanet has been rebuilt or skipped.</summary>
        static bool s_SgtPlanetsWarmed;

        /// <summary>Planet entities whose SgtPlanet we already probed this join.</summary>
        static readonly HashSet<Entity> s_SgtWarmedPlanets = new HashSet<Entity>();

        /// <summary>Reused planet-entity list for SGT warmup (no alloc per tick).</summary>
        static readonly List<Entity> s_PlanetEntityScratch = new List<Entity>(32);

        /// <summary>Moon-shield prefabs waiting for one Instantiates + draw.</summary>
        static readonly List<GameObject> s_MoonShieldPrefabs = new List<GameObject>(4);

        /// <summary>How many moon-shield prefabs we have Instantiates/drawn.</summary>
        static int s_MoonShieldsWarmed;

        /// <summary>True after we copied unique shield prefabs (or found none).</summary>
        static bool s_MoonShieldsQueued;

        /// <summary>
        /// One budgeted warmup slice. Safe to call twice in the same Unity frame — the second
        /// call no-ops. Publishes progress onto <see cref="PresentationJoinWarmupGate"/>.
        /// </summary>
        public static void Tick()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                PresentationJoinWarmupGate.Publish(true, 1f, "Graphics ready");
                return;
            }

            // --- One tick per rendered frame ---
            // LoadingScreen.Update and NceGameFlowController both Tick the gate.
            int frame = Time.frameCount;
            if (s_LastTickFrame == frame)
                return;
            s_LastTickFrame = frame;

            if (s_Finished)
            {
                PresentationJoinWarmupGate.Publish(true, 1f, "Graphics ready");
                return;
            }

            if (s_StartedRealtime < 0f)
                s_StartedRealtime = Time.realtimeSinceStartup;

            // --- Timeout so a missing prefab cannot soft-lock Join Team ---
            if (Time.realtimeSinceStartup - s_StartedRealtime >= TimeoutSeconds)
            {
                Debug.LogWarning(
                    "[PresentationJoinWarmup] Timed out after " + TimeoutSeconds +
                    "s — Join Team will open; leftover shaders compile on first spawn draw.");
                Finish(timedOut: true);
                return;
            }

            EnsureWarmupRig();

            // --- Collect (budgeted) then draw ---
            CollectGemTintsOnce();
            CollectProxyMaterials();
            TryCloseProxyScan();
            CollectVfxPrefabMaterials();
            TryCreateHullProbe();
            AdvanceHullTeamPaint();
            TryEnqueueVfxPrewarm();
            QueueMoonShieldPrefabsOnce();
            BulletOneShotVfxPool.TickPrewarm(VfxInstantiatesPerFrame);
            DrawQueuedMaterials();
            TryDrawOneVfxShell();
            TryRebuildOneDirtyPlanet();
            TryWarmOneMoonShield();

            if (IsWorkComplete())
            {
                Finish(timedOut: false);
                return;
            }

            PublishProgress();
        }

        /// <summary>
        /// Session leave: destroy hidden GOs and clear queues so the next join starts clean.
        /// In-process GPU programs stay cached — the second join should be a short no-op drain.
        /// </summary>
        public static void ResetSession()
        {
            TeardownRig();
            s_LastTickFrame = -1;
            s_StartedRealtime = -1f;
            s_Finished = false;
            s_HullFamily = null;
            s_NextHullTeam = TeamId.TeamA;
            s_HullDone = false;
            s_VfxEnqueued = false;
            s_GemTintsEnqueued = false;
            s_VfxCollectCursor = 0;
            s_VfxShellsRendered = 0;
            s_MaterialsRendered = 0;
            s_ProxyScanClosed = false;
            s_SgtPlanetsWarmed = false;
            s_MoonShieldsQueued = false;
            s_MoonShieldsWarmed = 0;
            s_SgtWarmedPlanets.Clear();
            s_PlanetEntityScratch.Clear();
            s_MoonShieldPrefabs.Clear();
            s_MaterialQueue.Clear();
            s_SeenMaterialIds.Clear();
            s_ScannedProxyEntities.Clear();
            s_VfxShellPrefabs.Clear();
            s_SeenVfxPrefabIds.Clear();
        }

#if UNITY_EDITOR
        /// <summary>[UNITY] Domain Reload off leaves static GO refs and flags sticky.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ResetSession();
#endif

        /// <summary>
        /// True when material / hull / VFX queues are idle. Proxy scans may still be mid-map;
        /// we finish once every <b>currently live</b> proxy has been scanned and the draw queue
        /// is empty — leftover Instantiates after Join Team compile on first nearby draw.
        /// </summary>
        static bool IsWorkComplete()
        {
            if (!s_HullDone)
                return false;
            if (!s_VfxEnqueued)
                return false;
            if (!BulletOneShotVfxPool.PrewarmComplete)
                return false;
            if (s_MaterialQueue.Count > 0)
                return false;
            if (s_VfxShellsRendered < s_VfxShellPrefabs.Count)
                return false;
            if (!s_SgtPlanetsWarmed)
                return false;
            if (!s_MoonShieldsQueued || s_MoonShieldsWarmed < s_MoonShieldPrefabs.Count)
                return false;

            // --- Do not finish before one full scan of the ready map ---
            // [TITAN-ORBIT] Instantiates of rocks continue until ~92% proxies. Completing
            // earlier would miss the materials the spawn camera actually sees. After that
            // first full scan we close — leftover distance-importance Instantiates must
            // not hold Join Team open.
            if (!s_ProxyScanClosed)
                return false;

            return true;
        }

        /// <summary>Tears down hidden GOs and publishes a terminal complete state.</summary>
        /// <param name="timedOut">True when the 15s cap fired (Join Team must still open).</param>
        static void Finish(bool timedOut)
        {
            s_Finished = true;
            TeardownRig();
            string status = timedOut ? "Graphics warmup timed out" : "Graphics ready";
            PresentationJoinWarmupGate.Publish(true, 1f, status);
        }

        /// <summary>Honest 0–1 fill + "Warming graphics  done / total" for the loading bar.</summary>
        static void PublishProgress()
        {
            int materialTotal = s_MaterialsRendered + s_MaterialQueue.Count;
            int vfxShellTotal = s_VfxShellPrefabs.Count;
            int done =
                s_MaterialsRendered +
                (s_HullDone ? 1 : 0) +
                s_VfxShellsRendered +
                (BulletOneShotVfxPool.PrewarmComplete ? 1 : 0);
            int total =
                Mathf.Max(1, materialTotal) +
                1 +
                Mathf.Max(1, vfxShellTotal) +
                1;

            float vfxPool = BulletOneShotVfxPool.PrewarmProgress;
            float materialFrac = materialTotal > 0
                ? (float)s_MaterialsRendered / materialTotal
                : (s_GemTintsEnqueued ? 1f : 0f);
            float hullFrac = s_HullDone ? 1f : (s_HullProbe != null ? 0.5f : 0f);
            float sgtFrac = s_SgtPlanetsWarmed ? 1f : 0.4f;
            float shieldFrac = s_MoonShieldsQueued
                ? (s_MoonShieldPrefabs.Count > 0
                    ? (float)s_MoonShieldsWarmed / s_MoonShieldPrefabs.Count
                    : 1f)
                : 0f;
            float combined = Mathf.Clamp01(
                0.30f * materialFrac +
                0.18f * hullFrac +
                0.20f * vfxPool +
                0.08f * (vfxShellTotal > 0 ? (float)s_VfxShellsRendered / vfxShellTotal : 0f) +
                0.16f * sgtFrac +
                0.08f * shieldFrac);

            PresentationJoinWarmupGate.Publish(
                false,
                combined,
                "Warming graphics  " + done + " / " + total);
        }

        /// <summary>
        /// Creates the off-camera rig once: disabled Camera + one quad on <see cref="WarmupLayer"/>.
        /// [UNITY] <c>Camera.enabled = false</c> so the player loop does not present this view;
        /// we call <c>Render()</c> only when we have a material to compile.
        /// </summary>
        static void EnsureWarmupRig()
        {
            if (s_Camera != null && s_QuadRenderer != null)
                return;

            TeardownRig();

            s_RigRoot = new GameObject("PresentationJoinWarmupRig");
            Object.DontDestroyOnLoad(s_RigRoot);
            s_RigRoot.hideFlags = HideFlags.HideAndDontSave;
            s_RigRoot.transform.position = WarmupOrigin;

            var camGo = new GameObject("WarmupCamera");
            camGo.transform.SetParent(s_RigRoot.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, -4f);
            camGo.layer = WarmupLayer;
            s_Camera = camGo.AddComponent<Camera>();
            s_Camera.enabled = false;
            s_Camera.clearFlags = CameraClearFlags.SolidColor;
            s_Camera.backgroundColor = Color.black;
            s_Camera.cullingMask = 1 << WarmupLayer;
            s_Camera.orthographic = true;
            s_Camera.orthographicSize = 4f;
            s_Camera.nearClipPlane = 0.1f;
            s_Camera.farClipPlane = 32f;
            s_Camera.allowHDR = false;
            s_Camera.allowMSAA = false;
            s_Camera.depth = -100;

            // --- Isolate URP from the Game-view overlay ---
            // [UNITY] Camera.Render() without a targetTexture blits into the play-mode
            // backbuffer. URP then draws UIToolkit/uGUI Overlay at a different size
            // (console: attachment 2560x1293 vs 2560x1248) and the warmup does not
            // compile gameplay shaders. A 64² RT + Base camera with an empty stack
            // keeps the draw off-screen.
            if (s_WarmupRt == null)
            {
                s_WarmupRt = new RenderTexture(WarmupRtSize, WarmupRtSize, 16)
                {
                    name = "PresentationJoinWarmupRT",
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 1,
                };
                s_WarmupRt.Create();
            }

            s_Camera.targetTexture = s_WarmupRt;

            var urp = camGo.GetComponent<UniversalAdditionalCameraData>();
            if (urp == null)
                urp = camGo.AddComponent<UniversalAdditionalCameraData>();
            urp.renderType = CameraRenderType.Base;
            urp.renderPostProcessing = false;
            urp.antialiasing = AntialiasingMode.None;
            urp.renderShadows = false;
            urp.dithering = false;
            if (urp.cameraStack != null)
                urp.cameraStack.Clear();

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "WarmupQuad";
            quad.transform.SetParent(s_RigRoot.transform, false);
            quad.transform.localPosition = Vector3.zero;
            SetLayerRecurse(quad, WarmupLayer);
            var col = quad.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
            s_QuadRenderer = quad.GetComponent<Renderer>();
        }

        /// <summary>Destroys the hidden camera, quad, and hull probe (safe if already gone).</summary>
        static void TeardownRig()
        {
            if (s_HullProbe != null)
            {
                Object.Destroy(s_HullProbe);
                s_HullProbe = null;
            }

            s_QuadRenderer = null;
            if (s_Camera != null)
                s_Camera.targetTexture = null;
            s_Camera = null;
            if (s_WarmupRt != null)
            {
                s_WarmupRt.Release();
                Object.Destroy(s_WarmupRt);
                s_WarmupRt = null;
            }

            if (s_RigRoot != null)
            {
                Object.Destroy(s_RigRoot);
                s_RigRoot = null;
            }
        }

        /// <summary>
        /// [UNITY] Builds the shared gem tint materials (keywords + blend) then queues them
        /// for a real <c>Camera.Render</c>. Instantiates-only warmup left the first gem blank.
        /// </summary>
        static void CollectGemTintsOnce()
        {
            if (s_GemTintsEnqueued)
                return;

            GemVisualApplier.EnsureSharedTintReady(GemVisualApplier.LoadDefaultGemPrefab());
            if (GemVisualApplier.TryGetSharedTintMaterials(out Material normal, out Material bonus))
            {
                EnqueueMaterial(normal);
                EnqueueMaterial(bonus);
            }

            s_GemTintsEnqueued = true;
        }

        /// <summary>
        /// Latches <see cref="s_ProxyScanClosed"/> once the map GO build is ready and every
        /// live proxy has been sampled. Further Instantiates are ignored for the gate.
        /// </summary>
        static void TryCloseProxyScan()
        {
            if (s_ProxyScanClosed)
                return;
            if (!EcsGameBridge.IsMapProxyCountReady(out _, out _, out _))
                return;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null && !visualizer.HaveScannedAllProxies(s_ScannedProxyEntities))
                return;

            s_ProxyScanClosed = true;
        }

        /// <summary>
        /// Walks hybrid map proxies already Instantiates this join (planets, asteroids, gems)
        /// and queues each unique <c>sharedMaterial</c>. Caps GO scans so a 300-rock map does
        /// not hitch one overlay frame.
        /// </summary>
        static void CollectProxyMaterials()
        {
            if (s_ProxyScanClosed)
                return;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return;

            visualizer.EnqueueUniqueProxySharedMaterials(
                s_MaterialQueue,
                s_SeenMaterialIds,
                s_ScannedProxyEntities,
                maxNewMaterials: 24,
                maxNewProxies: ProxyScansPerFrame);
        }

        /// <summary>
        /// Reads muzzle / impact / tracer prefab renderers from <see cref="BulletVfxBank"/>
        /// without Instantiates. Also records unique prefabs so we can Rent one idle shell
        /// later for particle-shader draws.
        /// </summary>
        static void CollectVfxPrefabMaterials()
        {
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return;

            int catCount = bank.CategoryCount;
            int teamCount = (int)TeamId.TeamE - (int)TeamId.TeamA + 1;
            int kinds = 3;
            int total = catCount * teamCount * kinds;
            int collected = 0;

            while (collected < VfxPrefabsCollectedPerFrame && s_VfxCollectCursor < total)
            {
                int cursor = s_VfxCollectCursor++;
                int kind = cursor % kinds;
                int rest = cursor / kinds;
                int teamOffset = rest % teamCount;
                int category = rest / teamCount;
                var team = (TeamId)((int)TeamId.TeamA + teamOffset);

                GameObject prefab = kind switch
                {
                    0 => bank.GetMuzzlePrefab(category, team),
                    1 => bank.GetImpactPrefab(category, team),
                    _ => bank.GetProjectileVisualPrefab(category, team),
                };
                if (prefab == null)
                    continue;

                EnqueuePrefabRendererMaterials(prefab);
                if (!Application.isMobilePlatform)
                    RememberVfxPrefab(prefab);
                collected++;
            }
        }

        /// <summary>
        /// Instantiates the default starting hull (AstroEagle / visualizer family) off-camera
        /// so ship shaders compile before <c>CreateShipProxy</c> at spawn.
        /// </summary>
        static void TryCreateHullProbe()
        {
            if (s_HullDone || s_HullProbe != null)
                return;

            ShipFamilyDefinition family = ResolveStartingFamily();
            if (family == null)
            {
                s_HullDone = true;
                return;
            }

            if (!ShipVisualApplier.TryCreateShipVisualForChassis(
                    family,
                    prefabOverride: null,
                    TeamId.TeamA,
                    shipLevel: 1,
                    chassisId: null,
                    out GameObject hull) ||
                hull == null)
            {
                s_HullDone = true;
                return;
            }

            s_HullFamily = family;
            s_HullProbe = hull;
            s_HullProbe.name = "PresentationJoinWarmupHull";
            s_HullProbe.hideFlags = HideFlags.HideAndDontSave;
            s_HullProbe.transform.position = WarmupOrigin;
            SetLayerRecurse(s_HullProbe, WarmupLayer);
            EnqueueRendererMaterials(s_HullProbe);
            RenderObject(s_HullProbe);
            s_NextHullTeam = TeamId.TeamB;
        }

        /// <summary>
        /// Paints the next team palette on the hull (B–E) so those sharedMaterials compile too.
        /// One team per tick. Destroys the probe after Team E.
        /// </summary>
        static void AdvanceHullTeamPaint()
        {
            if (s_HullDone || s_HullProbe == null)
                return;
            if (s_NextHullTeam < TeamId.TeamB || s_NextHullTeam > TeamId.TeamE)
                return;

            ShipVisualApplier.ApplyTeamMaterials(s_HullFamily, s_HullProbe, s_NextHullTeam);
            EnqueueRendererMaterials(s_HullProbe);
            RenderObject(s_HullProbe);

            if (s_NextHullTeam >= TeamId.TeamE)
            {
                Object.Destroy(s_HullProbe);
                s_HullProbe = null;
                s_HullFamily = null;
                s_HullDone = true;
                return;
            }

            s_NextHullTeam = (TeamId)((int)s_NextHullTeam + 1);
        }

        /// <summary>
        /// Prefers the live visualizer family (inspector / Awake default), then home-slot
        /// AstroEagle from <see cref="PlanetShipFamilyConfig"/>.
        /// </summary>
        static ShipFamilyDefinition ResolveStartingFamily()
        {
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null && visualizer.ResolvedShipFamily != null)
                return visualizer.ResolvedShipFamily;

            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            if (config == null)
                return null;

            ShipFamilyDefinition astro = config.GetFamilyDefinitionByFamilyId("AstroEagle");
            if (astro != null)
                return astro;

            var home = config.GetFamilyByConfigIndex(0);
            return home != null ? home.shipFamilyDefinition : null;
        }

        /// <summary>
        /// Asks <see cref="BulletVfxDriver"/> to enqueue the banked muzzle/impact/tracer
        /// Instantiates so <see cref="BulletOneShotVfxPool.TickPrewarm"/> can drain under
        /// the overlay even if the driver's LateUpdate has not run yet.
        /// </summary>
        static void TryEnqueueVfxPrewarm()
        {
            if (s_VfxEnqueued)
                return;
            if (!BulletVfxDriver.TryEnqueueJoinLoadVfxPrewarm())
                return;
            s_VfxEnqueued = true;
        }

        /// <summary>
        /// Assigns the next queued materials onto the warmup quad and <c>Camera.Render</c>s.
        /// Four draws per frame keeps overlay hitch off the map Instantiates drain.
        /// </summary>
        static void DrawQueuedMaterials()
        {
            if (s_Camera == null || s_QuadRenderer == null)
                return;

            int drawn = 0;
            while (drawn < MaterialsPerFrame && s_MaterialQueue.Count > 0)
            {
                Material material = s_MaterialQueue[s_MaterialQueue.Count - 1];
                s_MaterialQueue.RemoveAt(s_MaterialQueue.Count - 1);
                if (material == null)
                    continue;

                s_QuadRenderer.sharedMaterial = material;
                s_Camera.Render();
                s_MaterialsRendered++;
                drawn++;
            }
        }

        /// <summary>
        /// Rents one idle VFX shell (already Instantiates by TickPrewarm), draws it with the
        /// warmup camera so particle programs compile, then Returns it to the pool.
        /// </summary>
        static void TryDrawOneVfxShell()
        {
            if (s_Camera == null)
                return;
            if (s_VfxShellsRendered >= s_VfxShellPrefabs.Count)
                return;

            GameObject prefab = s_VfxShellPrefabs[s_VfxShellsRendered];
            if (prefab == null)
            {
                s_VfxShellsRendered++;
                return;
            }

            if (!BulletOneShotVfxPool.TryRentIdle(prefab, out GameObject shell) || shell == null)
            {
                // Prewarm has not Instantiates this prefab yet — retry next frame.
                // Once the queue is empty, skip leftovers so we cannot stall Join Team.
                if (BulletOneShotVfxPool.PrewarmComplete)
                    s_VfxShellsRendered++;
                return;
            }

            shell.transform.position = WarmupOrigin;
            SetLayerRecurse(shell, WarmupLayer);
            s_Camera.Render();
            // [UNITY] Combat cameras render the Default layer — put the shell back before Return.
            SetLayerRecurse(shell, 0);
            BulletOneShotVfxPool.ReturnNow(shell);
            s_VfxShellsRendered++;
        }

        /// <summary>Looks at <paramref name="root"/> with a slightly larger ortho and draws once.</summary>
        static void RenderObject(GameObject root)
        {
            if (s_Camera == null || root == null)
                return;

            float previous = s_Camera.orthographicSize;
            s_Camera.orthographicSize = 8f;
            s_Camera.Render();
            s_Camera.orthographicSize = previous;
        }

        /// <summary>Queues <paramref name="material"/> once (InstanceID dedupe).</summary>
        static void EnqueueMaterial(Material material)
        {
            if (material == null)
                return;
            int id = material.GetInstanceID();
            if (!s_SeenMaterialIds.Add(id))
                return;
            s_MaterialQueue.Add(material);
        }

        /// <summary>Queues every renderer <c>sharedMaterial</c> on an instance or prefab.</summary>
        static void EnqueueRendererMaterials(GameObject root)
        {
            if (root == null)
                return;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                for (int m = 0; m < mats.Length; m++)
                    EnqueueMaterial(mats[m]);
            }
        }

        /// <summary>Same as <see cref="EnqueueRendererMaterials"/> but also works on prefab assets.</summary>
        static void EnqueuePrefabRendererMaterials(GameObject prefab) =>
            EnqueueRendererMaterials(prefab);

        /// <summary>Remembers a unique VFX prefab for a later one-shell <c>Camera.Render</c>.</summary>
        static void RememberVfxPrefab(GameObject prefab)
        {
            if (prefab == null)
                return;
            int id = prefab.GetInstanceID();
            if (!s_SeenVfxPrefabIds.Add(id))
                return;
            s_VfxShellPrefabs.Add(prefab);
        }

        /// <summary>
        /// Copies unique MatrixShield prefabs once so we can Instantiates + draw each under
        /// the overlay. Profiler: first <c>GemMoonMatrixShieldVisual</c> Instantiates of
        /// MatrixShieldRed cost ~95 ms after spawn.
        /// </summary>
        static void QueueMoonShieldPrefabsOnce()
        {
            if (s_MoonShieldsQueued)
                return;

            GemMoonShieldPrefabLibrary.CopyUniquePrefabs(s_MoonShieldPrefabs);
            s_MoonShieldsQueued = true;
        }

        /// <summary>
        /// Instantiates one moon-shield prefab, draws it on the warmup camera, then destroys
        /// the probe. The live moon visual Instantiates later from the same prefab — shaders
        /// and Awake cost are already paid.
        /// </summary>
        static void TryWarmOneMoonShield()
        {
            if (!s_MoonShieldsQueued || s_Camera == null)
                return;
            if (s_MoonShieldsWarmed >= s_MoonShieldPrefabs.Count)
                return;

            GameObject prefab = s_MoonShieldPrefabs[s_MoonShieldsWarmed];
            s_MoonShieldsWarmed++;
            if (prefab == null)
                return;

            var probe = Object.Instantiate(prefab);
            probe.name = "PresentationJoinWarmupMoonShield";
            probe.hideFlags = HideFlags.HideAndDontSave;
            probe.transform.position = WarmupOrigin;
            SetLayerRecurse(probe, WarmupLayer);
            EnqueueRendererMaterials(probe);
            RenderObject(probe);
            Object.Destroy(probe);
        }

        /// <summary>
        /// Rebuilds at most one dirty planet <see cref="SgtPlanet"/> this frame.
        /// After map proxies are ready, a full pass with no remaining dirty planets
        /// latches <see cref="s_SgtPlanetsWarmed"/>. Asteroids are skipped — homes
        /// are what the spawn camera sees first.
        /// </summary>
        static void TryRebuildOneDirtyPlanet()
        {
            if (s_SgtPlanetsWarmed)
                return;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
            {
                if (EcsGameBridge.IsMapProxyCountReady(out _, out _, out _))
                    s_SgtPlanetsWarmed = true;
                return;
            }

            visualizer.CopyPlanetProxyEntities(s_PlanetEntityScratch);
            int rebuilt = 0;
            bool pending = false;
            for (int i = 0; i < s_PlanetEntityScratch.Count; i++)
            {
                Entity entity = s_PlanetEntityScratch[i];
                if (s_SgtWarmedPlanets.Contains(entity))
                    continue;
                if (!visualizer.TryGetProxy(entity, out GameObject proxy) || proxy == null)
                {
                    s_SgtWarmedPlanets.Add(entity);
                    continue;
                }

                var planets = proxy.GetComponentsInChildren<SgtPlanet>(true);
                bool needed = false;
                for (int p = 0; p < planets.Length; p++)
                {
                    if (!SgtPlanetMeshWarm.NeedsRebuild(planets[p]))
                        continue;
                    needed = true;
                    if (rebuilt >= SgtPlanetRebuildsPerFrame)
                    {
                        pending = true;
                        break;
                    }

                    planets[p].Rebuild();
                    rebuilt++;
                }

                if (pending)
                    break;
                s_SgtWarmedPlanets.Add(entity);
            }

            if (!pending && EcsGameBridge.IsMapProxyCountReady(out _, out _, out _))
            {
                bool allSeen = true;
                for (int i = 0; i < s_PlanetEntityScratch.Count; i++)
                {
                    if (s_SgtWarmedPlanets.Contains(s_PlanetEntityScratch[i]))
                        continue;
                    allSeen = false;
                    break;
                }

                if (allSeen)
                    s_SgtPlanetsWarmed = true;
            }
        }

        /// <summary>[UNITY] Warmup camera culls by layer — children must match the parent.</summary>
        static void SetLayerRecurse(GameObject go, int layer)
        {
            if (go == null)
                return;
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecurse(t.GetChild(i).gameObject, layer);
        }
    }
}
