using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-only Shapes drawer for wing tractor beams.
    /// Deploy beat: thin line shoots wing mid-center→gem, cone mouth opens at 90% of gem diameter,
    /// then server/client pull begins. Start is pointy on the wing body center; gem end stays rounded.
    /// Pairs with <see cref="GemTractorBeamClientLogic"/>, <see cref="GemTractorBeamDeployTracker"/>,
    /// and <see cref="GemTractorBeamVisibilityTracker"/>. Cosmetic only — pull is
    /// <c>GemTractorBeamSystem</c> on the server.
    /// </summary>
    [ExecuteAlways]
    public class GemTractorBeamVisual : ImmediateModeShapeDrawer
    {
        /// <summary>Singleton drawer instance — auto-created on <see cref="RuntimeInitializeOnLoadMethod"/>.</summary>
        static GemTractorBeamVisual _instance;

        [Header("Beam")]
        /// <summary>
        /// Shared Y lift above the wing mid-center. Keep at 0 so the cone start touches the wing
        /// with no gap; raise only if deck z-fight appears.
        /// </summary>
        [SerializeField] float heightAboveWing = 0f;
        [SerializeField] float pulseWidthAmplitude = 0.06f;
        [SerializeField] float pulseAlphaAmplitude = 0.14f;
        [SerializeField] float pulseSpeed = 2f;
        // [TITAN-ORBIT] Alphas = prior defaults at half opacity (50% more transparent / see-through).
        // ship 0.72→0.36, gem 0.16→0.08, extend 0.85→0.425.
        [SerializeField] float alphaAtShip = 0.36f;
        [SerializeField] float alphaAtGem = 0.08f;
        [SerializeField] float alphaExtendLine = 0.425f;
        [SerializeField] float extendLineThickness = 0.07f;
        [SerializeField] Color bonusBeamTint = new Color(1f, 0.92f, 0.35f, 1f);
        [SerializeField] bool gameplayCamerasOnly = true;
        /// <summary>
        /// Soft disc radius at the gem mouth as a fraction of mouth width. Does not use Triangle
        /// roundness — that rounds the wing apex too and left a gap once the cone opened.
        /// </summary>
        [SerializeField] [Range(0f, 1f)] float gemMouthRoundness = 0.55f;
        /// <summary>
        /// Cone mouth as a fraction of gem world diameter.
        /// [TITAN-ORBIT] 0.9 = 10% narrower than the gem so the flat edge sits inside the crystal.
        /// </summary>
        [SerializeField] [Range(0.5f, 1.2f)] float gemEndWidthScale = 0.9f;

        /// <summary>Per-gem smoothed visual diameter so beam width does not pop when proxy scale updates.</summary>
        static readonly Dictionary<int, float> SmoothedGemDiameterById = new Dictionary<int, float>(64);
        const float GemDiameterSmoothing = 10f;

        /// <summary>
        /// [UNITY] Ensures a scene drawer exists after load — beams are global, not per-ship prefab.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstanceExists()
        {
            if (_instance != null)
                return;

            // --- Reuse existing instance if designer placed one ---
            _instance = FindAnyObjectByType<GemTractorBeamVisual>();
            if (_instance != null)
                return;

            // --- Attach to shared planet-connection root when present ---
            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            if (go.GetComponent<GemTractorBeamVisual>() == null)
                _instance = go.AddComponent<GemTractorBeamVisual>();
        }

        public override void OnEnable()
        {
            _instance = this;
            base.OnEnable();
        }

        public override void OnDisable()
        {
            if (_instance == this)
                _instance = null;
            GemTractorBeamVisibilityTracker.Clear();
            SmoothedGemDiameterById.Clear();
            base.OnDisable();
        }

        /// <summary>[UNITY] Advances fade/deploy trackers once per frame before Shapes draws.</summary>
        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            GemTractorBeamVisibilityTracker.LateUpdateTick();
        }

        /// <summary>
        /// Scratch list for quarantine-safe gem proxies (hybrid registry — not ECS ToEntityArray).
        /// </summary>
        static readonly List<GemTractorBeamClientLogic.GemProxySnapshot> GemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);

        /// <summary>
        /// [HYBRID] Per-camera immediate-mode draw pass — ships via tiny query; gems via hybrid proxies.
        /// Renders one Shapes beam per assigned wing→gem pair.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            // --- Camera filter ---
            if (cam == null)
                return;
            if (gameplayCamerasOnly && !IsGameplayCamera(cam))
                return;

            // [TITAN-ORBIT] Settling OR GhostSpawnBacklog. TransformQuarantine is session-long on
            // Windows — beams must draw after settle using gem proxies (never full gem ToEntityArray).
            // Ship ToEntityArray during post–Join Team Instantiates → Crash!!! (2026-07-19).
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            // --- Visualization ECS world (client presentation) ---
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;

            using var shipQuery = em.CreateEntityQuery(typeof(ShipTag), typeof(ShipState), typeof(LocalTransform));
            using var ships = shipQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Unity.Collections.Allocator.Temp);
            using var shipTransforms = shipQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            GemTractorBeamClientLogic.CollectGemProxies(em, GemScratch);

            if (ships.Length == 0 || GemScratch.Count == 0)
                return;

            float pulseWave = Mathf.SmoothStep(0f, 1f, (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
            float pulsedAlphaAtShip = Mathf.Clamp01(alphaAtShip + (pulseWave * 2f - 1f) * pulseAlphaAmplitude);
            // [TITAN-ORBIT] Pulse only thins the mouth — never wider than gemEndWidthScale (10% under gem).
            float widthPulse = 1f - (1f - pulseWave) * pulseWidthAmplitude;

            // --- Shapes draw scope for this camera ---
            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.BlendMode = ShapesBlendMode.Transparent;

                // --- Ship × gem pairs: eligibility, range, visibility, deploy phase ---
                for (int si = 0; si < ships.Length; si++)
                {
                    if (!GemTractorBeamClientLogic.IsShipEligibleForBeam(shipStates[si]))
                        continue;

                    Color teamBase = shipStates[si].Team != TeamId.None
                        ? shipStates[si].Team.ToColor()
                        : new Color(0.85f, 0.95f, 1f);

                    var wings = em.HasBuffer<ShipWingTractorBeamElement>(ships[si])
                        ? em.GetBuffer<ShipWingTractorBeamElement>(ships[si])
                        : default;

                    // One draw per wing↔gem pair (sticky primary + spare assists on the same gem).
                    if (!GemTractorBeamClientLogic.TryGetShipBeamPairs(ships[si].Index, out var beamPairs))
                        continue;

                    for (int pi = 0; pi < beamPairs.Count; pi++)
                    {
                        var pair = beamPairs[pi];
                        if (!TryFindGemSnapshot(pair.GemId, out var gem))
                            continue;
                        if (!GemTractorBeamClientLogic.IsWithinMagneticPullRange(
                                em, ships[si], shipStates[si], shipTransforms[si], wings,
                                gem.Entity, gem.Transform, mapW, mapH))
                            continue;

                        float beamVisibility = GemTractorBeamVisibilityTracker.GetVisibility(ships[si].Index, gem.Entity.Index);
                        if (beamVisibility <= 0.001f)
                            continue;

                        float3 beamOriginLogical = GemTractorBeamClientLogic.ResolveBeamOriginForWing(
                            shipTransforms[si], wings, pair.WingIndex);
                        Vector3 reference = ToroidalDisplay.TryGetReferencePosition(out var shipRef)
                            ? shipRef
                            : cam.transform.position;

                        Vector3 shipDisplay = ResolveWingBeamOriginDisplay(
                            em, ships[si], wings, pair.WingIndex, beamOriginLogical, reference);

                        Vector3 gemDisplay = ResolveGemBeamTipDisplay(
                            gem.Entity, gem.Transform.Position, beamOriginLogical, shipDisplay, mapW, mapH);

                        float beamY = shipDisplay.y + heightAboveWing;
                        shipDisplay.y = beamY;
                        gemDisplay.y = beamY;

                        float extension = GemTractorBeamDeployTracker.GetExtensionProgress(ships[si].Index, gem.Entity.Index);
                        float widthExpand = GemTractorBeamDeployTracker.GetWidthExpandProgress(ships[si].Index, gem.Entity.Index);
                        float extensionEased = Mathf.SmoothStep(0f, 1f, extension);
                        Vector3 tipDisplay = Vector3.Lerp(shipDisplay, gemDisplay, extensionEased);

                        float gemDiameter = GetSmoothedGemVisualDiameter(gem.Entity, gem.State);
                        float widthAtGem = gemDiameter * gemEndWidthScale * widthPulse;

                        if (extensionEased < 1f - 0.0001f)
                        {
                            float extendVis = GemTractorBeamDeployTracker.IsInDeployAnimation(ships[si].Index, gem.Entity.Index)
                                ? 1f
                                : Mathf.Max(beamVisibility, 0.85f);
                            Color extendColor = new Color(teamBase.r, teamBase.g, teamBase.b, alphaExtendLine * extendVis);
                            DrawExtendLineWithWraps(shipDisplay, tipDisplay, mapW, mapH, extendColor);
                            continue;
                        }

                        float thinWidth = Mathf.Max(extendLineThickness, GemTractorBeamDeployTracker.ExtendLineThickness);
                        float widthEased = Mathf.SmoothStep(0f, 1f, widthExpand);
                        float currentWidth = Mathf.Lerp(thinWidth, widthAtGem, widthEased);

                        Color colorShip = new Color(teamBase.r, teamBase.g, teamBase.b, pulsedAlphaAtShip * beamVisibility);
                        Color colorGem = new Color(teamBase.r, teamBase.g, teamBase.b, alphaAtGem * beamVisibility);
                        DrawConeBeamWithWraps(shipDisplay, gemDisplay, mapW, mapH, currentWidth, colorShip, colorGem);
                    }
                }
            }
        }

        static bool IsGameplayCamera(Camera cam)
        {
            if (cam.cameraType != CameraType.Game)
                return false;
            if (cam.targetTexture != null)
                return false;
            return true;
        }

        /// <summary>Finds a gem snapshot by entity index from the current <see cref="GemScratch"/> gather.</summary>
        static bool TryFindGemSnapshot(int gemIndex, out GemTractorBeamClientLogic.GemProxySnapshot gem)
        {
            for (int i = 0; i < GemScratch.Count; i++)
            {
                if (GemScratch[i].Entity.Index != gemIndex)
                    continue;
                gem = GemScratch[i];
                return true;
            }

            gem = default;
            return false;
        }

        /// <summary>
        /// Display-space wing <b>mid-center</b>: hybrid hull proxy renderer bounds center
        /// (not the wing tip pivot), else toroidal display of the ECS logical wing origin.
        /// </summary>
        static Vector3 ResolveWingBeamOriginDisplay(
            EntityManager em,
            Entity shipEntity,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            int wingIndex,
            float3 beamOriginLogical,
            Vector3 reference)
        {
            // --- Path A: live hull proxy wing body mid-center ---
            if (TryGetLiveWingMidCenterWorld(em, shipEntity, wings, wingIndex, out Vector3 wingWorld))
                return wingWorld;

            // --- Path B: ECS logical wing → display tile ---
            return ToroidalDisplay.ToDisplayPosition(beamOriginLogical, reference);
        }

        /// <summary>
        /// Finds the given wing on the hybrid hull and returns the mid-center of that wing body
        /// (outermost Wing-named ancestor + renderer bounds center — not a tip child pivot).
        /// </summary>
        static bool TryGetLiveWingMidCenterWorld(
            EntityManager em,
            Entity shipEntity,
            DynamicBuffer<ShipWingTractorBeamElement> wings,
            int wingIndex,
            out Vector3 wingWorld)
        {
            wingWorld = default;
            if (!wings.IsCreated || wingIndex < 0 || wingIndex >= wings.Length)
                return false;

            int networkId = 0;
            if (em.HasComponent<Unity.NetCode.GhostOwner>(shipEntity))
                networkId = em.GetComponentData<Unity.NetCode.GhostOwner>(shipEntity).NetworkId;
            if (networkId <= 0)
                networkId = EcsGameBridge.GetLocalNetworkId();

            if (networkId <= 0 ||
                !ShipWeaponProxyRegistry.TryGetHull(networkId, out Transform hullRoot) ||
                hullRoot == null)
                return false;

            // Prefer authoring markers; match by hull-root local to the sim wing buffer slot.
            var wingAuths = hullRoot.GetComponentsInChildren<TitanOrbit.ECS.Authoring.ShipWingTractorBeamAuthoring>(true);
            float3 targetLocal = wings[wingIndex].LocalPosition;
            float bestDistSq = float.MaxValue;
            Transform bestMarker = null;

            if (wingAuths != null)
            {
                for (int i = 0; i < wingAuths.Length; i++)
                {
                    var auth = wingAuths[i];
                    if (auth == null || auth.transform == hullRoot)
                        continue;

                    ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                        hullRoot, auth.transform, out float3 localPos, out _);
                    float d = math.lengthsq(localPos - targetLocal);
                    if (d < bestDistSq)
                    {
                        bestDistSq = d;
                        bestMarker = auth.transform;
                    }
                }
            }

            // Index fallback when locals are ambiguous / no authoring yet.
            if (bestMarker == null && wingAuths != null && wingAuths.Length > 0)
            {
                int filtered = 0;
                for (int i = 0; i < wingAuths.Length; i++)
                {
                    var auth = wingAuths[i];
                    if (auth == null || auth.transform == hullRoot)
                        continue;
                    if (filtered == wingIndex)
                    {
                        bestMarker = auth.transform;
                        break;
                    }

                    filtered++;
                }
            }

            if (bestMarker == null)
                return false;

            // Climb to the outermost Wing-named body so tip/mesh children do not own the anchor.
            Transform wingBody = ResolveWingBodyRoot(bestMarker, hullRoot);
            wingWorld = GetRendererBoundsCenter(wingBody);
            return true;
        }

        /// <summary>
        /// Climb from a tip/marker to its single-wing body. Stops before multi-wing group parents
        /// (e.g. a "Wings" folder) — climbing into those put the beam origin at the ship center.
        /// </summary>
        static Transform ResolveWingBodyRoot(Transform wingMarker, Transform hullRoot)
        {
            Transform body = wingMarker;
            Transform candidate = wingMarker.parent;
            int markersInBody = CountWingAuthoringsUnder(body);

            while (candidate != null && candidate != hullRoot && IsWingBodyName(candidate.name))
            {
                int markersInCandidate = CountWingAuthoringsUnder(candidate);
                // Parent owns more wing slots than this branch → it is a wing group; stop.
                if (markersInCandidate > markersInBody)
                    break;

                body = candidate;
                markersInBody = markersInCandidate;
                candidate = candidate.parent;
            }

            return body;
        }

        static int CountWingAuthoringsUnder(Transform root)
        {
            if (root == null)
                return 0;

            var auths = root.GetComponentsInChildren<TitanOrbit.ECS.Authoring.ShipWingTractorBeamAuthoring>(true);
            int count = 0;
            for (int i = 0; i < auths.Length; i++)
            {
                if (auths[i] != null && auths[i].transform != null)
                    count++;
            }

            return count;
        }

        static bool IsWingBodyName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return name.IndexOf("Wing", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Mid-center of one wing: renderer bounds center when meshes exist, else the wing pivot.
        /// </summary>
        static Vector3 GetRendererBoundsCenter(Transform root)
        {
            if (root == null)
                return Vector3.zero;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds combined = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (renderer.bounds.size.sqrMagnitude < 1e-8f)
                    continue;

                if (!hasBounds)
                {
                    combined = renderer.bounds;
                    hasBounds = true;
                }
                else
                    combined.Encapsulate(renderer.bounds);
            }

            return hasBounds ? combined.center : root.position;
        }

        /// <summary>
        /// Display-space gem tip for the beam: prefer the live hybrid proxy (client pull pose),
        /// else toroidal nearest-tile from ECS logical position.
        /// </summary>
        static Vector3 ResolveGemBeamTipDisplay(
            Entity gemEntity,
            float3 gemLogicalPos,
            float3 beamOriginLogical,
            Vector3 shipDisplay,
            float mapW,
            float mapH)
        {
            // --- Path A: hybrid GO already in display space (GemClientMotionApplier) ---
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null &&
                visualizer.TryGetProxy(gemEntity, out GameObject proxy) &&
                proxy != null)
            {
                Vector3 proxyPos = proxy.transform.position;
                // Keep tip on the same map tile as the wing so a wrap copy of the mesh
                // cannot stretch the beam across the seam.
                float3 toProxy = ToroidalMapEcs.ShortestOffsetXZ(
                    new float3(shipDisplay.x, 0f, shipDisplay.z),
                    new float3(proxyPos.x, 0f, proxyPos.z),
                    mapW,
                    mapH);
                return shipDisplay + new Vector3(toProxy.x, 0f, toProxy.z);
            }

            // --- Path B: ECS logical → display relative to wing origin ---
            float3 gemOff = ToroidalMapEcs.ShortestOffsetXZ(beamOriginLogical, gemLogicalPos, mapW, mapH);
            return shipDisplay + new Vector3(gemOff.x, 0f, gemOff.z);
        }

        void DrawExtendLineWithWraps(Vector3 origin, Vector3 tip, float mapW, float mapH, Color color)
        {
            float thickness = Mathf.Max(extendLineThickness, GemTractorBeamDeployTracker.ExtendLineThickness);
            DrawExtendLineSegment(origin, tip, thickness, color);

            Vector3[] offsets =
            {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };

            for (int i = 0; i < offsets.Length; i++)
                DrawExtendLineSegment(origin + offsets[i], tip + offsets[i], thickness, color);
        }

        static void DrawExtendLineSegment(Vector3 origin, Vector3 tip, float thickness, Color color)
        {
            Vector3 dir = tip - origin;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0004f)
                return;

            // [TITAN-ORBIT] Shapes applies one LineEndCap to both ends — no per-end round.
            // Use None so neither end gets a round cap (keeps the wing attach point sharp).
            Draw.LineGeometry = LineGeometry.Volumetric3D;
            Draw.Line(origin, tip, thickness, LineEndCap.None, color);
            Draw.LineGeometry = LineGeometry.Flat2D;
        }

        void DrawConeBeamWithWraps(Vector3 shipDisplay, Vector3 gemDisplay, float mapW, float mapH,
            float widthAtGem, Color colorShip, Color colorGem)
        {
            DrawConeBeamSegment(shipDisplay, gemDisplay, widthAtGem, colorShip, colorGem);

            Vector3[] offsets =
            {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH),
            };

            for (int i = 0; i < offsets.Length; i++)
                DrawConeBeamSegment(shipDisplay + offsets[i], gemDisplay + offsets[i], widthAtGem, colorShip, colorGem);
        }

        void DrawConeBeamSegment(Vector3 shipDisplay, Vector3 gemDisplay, float widthAtGem, Color colorShip, Color colorGem)
        {
            Vector3 dir = gemDisplay - shipDisplay;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                return;

            dir.Normalize();
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
            float halfGem = widthAtGem * 0.5f;
            Vector3 gemLeft = gemDisplay - perp * halfGem;
            Vector3 gemRight = gemDisplay + perp * halfGem;

            // --- Sharp triangle: apex exactly on the wing (same point as the extend line) ---
            // [TITAN-ORBIT] Do NOT pass Triangle roundness — Shapes rounds every corner, which
            // pulled the wing apex inward and looked like a gap only after the cone opened.
            Draw.Triangle(shipDisplay, gemLeft, gemRight, colorShip, colorGem, colorGem);

            // --- Soft gem mouth only (wing stays pointy) ---
            if (gemMouthRoundness > 0.001f && widthAtGem > 0.001f)
            {
                float mouthRadius = halfGem * gemMouthRoundness;
                Draw.Disc(gemDisplay, Vector3.up, mouthRadius, colorGem);
            }
        }

        /// <summary>
        /// World-space diameter for the cone mouth: prefer live hybrid proxy bounds, then registry,
        /// then <see cref="GemState.Size"/> / value curve. Smoothed so scale changes do not pop.
        /// </summary>
        static float GetSmoothedGemVisualDiameter(Entity gemEntity, in GemState gemState)
        {
            float raw = ResolveGemWorldDiameter(gemEntity, gemState);
            // Floor so tiny gems still read as a cone, not a hairline.
            raw = Mathf.Max(0.12f, raw);

            int key = gemEntity.Index;
            if (!SmoothedGemDiameterById.TryGetValue(key, out float smoothed))
            {
                SmoothedGemDiameterById[key] = raw;
                return raw;
            }

            smoothed = Mathf.Lerp(smoothed, raw, Time.deltaTime * GemDiameterSmoothing);
            SmoothedGemDiameterById[key] = smoothed;
            return smoothed;
        }

        /// <summary>
        /// Picks the best available world diameter for one gem this frame.
        /// </summary>
        static float ResolveGemWorldDiameter(Entity gemEntity, in GemState gemState)
        {
            // --- Path A: live hybrid proxy renderer bounds (most accurate) ---
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer != null &&
                visualizer.TryGetProxy(gemEntity, out GameObject proxy) &&
                proxy != null)
            {
                float fromProxy = GemVisualApplier.ReadWorldDiameter(proxy, math.max(0.25f, gemState.Value));
                if (fromProxy > 0.01f)
                    return fromProxy;
            }

            // --- Path B: diameter registry written when the proxy was spawned/scaled ---
            if (GemVisualDiameterRegistry.TryGetDiameter(gemEntity, out float registered) && registered > 0.01f)
                return registered;

            // --- Path C: value → visual scale curve (same as GemVisualApplier) ---
            // [TITAN-ORBIT] Do not use GemState.Size * 2 — Size is sim LocalTransform scale
            // (often 0.2–0.5), not the hybrid GO diameter. Doubling it mismatched the crystal.
            return GemVisualApplier.ComputeVisualDiameter(math.max(0.25f, gemState.Value));
        }
    }
}
