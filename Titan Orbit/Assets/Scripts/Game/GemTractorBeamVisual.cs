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
    /// Draws wing tractor beams: thin extending line, width expansion at the gem, then a filled cone while pulling.
    /// </summary>
    [ExecuteAlways]
    public class GemTractorBeamVisual : ImmediateModeShapeDrawer
    {
        static GemTractorBeamVisual _instance;

        [Header("Beam")]
        [SerializeField] float heightAboveGem = 0.28f;
        [SerializeField] float pulseWidthAmplitude = 0.06f;
        [SerializeField] float pulseAlphaAmplitude = 0.14f;
        [SerializeField] float pulseSpeed = 2f;
        [SerializeField] float alphaAtShip = 0.72f;
        [SerializeField] float alphaAtGem = 0.16f;
        [SerializeField] float alphaExtendLine = 0.85f;
        [SerializeField] float extendLineThickness = 0.07f;
        [SerializeField] Color bonusBeamTint = new Color(1f, 0.92f, 0.35f, 1f);
        [SerializeField] bool gameplayCamerasOnly = true;
        [SerializeField] [Range(0f, 1f)] float beamRoundness = 0.08f;
        [SerializeField] float gemEndWidthScale = 1.05f;

        static readonly Dictionary<int, float> SmoothedGemDiameterById = new Dictionary<int, float>(64);
        const float GemDiameterSmoothing = 10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstanceExists()
        {
            if (_instance != null)
                return;

            _instance = FindAnyObjectByType<GemTractorBeamVisual>();
            if (_instance != null)
                return;

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

        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            GemTractorBeamVisibilityTracker.LateUpdateTick();
        }

        public override void DrawShapes(Camera cam)
        {
            if (cam == null)
                return;
            if (gameplayCamerasOnly && !IsGameplayCamera(cam))
                return;

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

            using var gemQuery = em.CreateEntityQuery(typeof(GemTag), typeof(GemState), typeof(LocalTransform));
            using var gems = gemQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            using var gemStates = gemQuery.ToComponentDataArray<GemState>(Unity.Collections.Allocator.Temp);
            using var gemTransforms = gemQuery.ToComponentDataArray<LocalTransform>(Unity.Collections.Allocator.Temp);

            if (ships.Length == 0 || gems.Length == 0)
                return;

            Vector3 camPos = cam.transform.position;
            float pulseWave = Mathf.SmoothStep(0f, 1f, (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
            float pulsedAlphaAtShip = Mathf.Clamp01(alphaAtShip + (pulseWave * 2f - 1f) * pulseAlphaAmplitude);
            float widthPulse = 1f + (pulseWave * 2f - 1f) * pulseWidthAmplitude;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.BlendMode = ShapesBlendMode.Transparent;

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

                    for (int gi = 0; gi < gems.Length; gi++)
                    {
                        if (!GemTractorBeamClientLogic.IsGemEligibleForBeam(gemStates[gi]))
                            continue;
                        if (!GemTractorBeamClientLogic.CanShipMagneticallyPull(ships[si].Index, gems[gi].Index))
                            continue;
                        if (!GemTractorBeamClientLogic.IsWithinMagneticPullRange(
                                em, ships[si], shipStates[si], shipTransforms[si], wings,
                                gems[gi], gemTransforms[gi], mapW, mapH))
                            continue;

                        float beamVisibility = GemTractorBeamVisibilityTracker.GetVisibility(ships[si].Index, gems[gi].Index);
                        if (beamVisibility <= 0.001f)
                            continue;

                        float3 beamOrigin = GemTractorBeamClientLogic.ResolveBeamOrigin(
                            ships[si], shipTransforms[si], wings, gems[gi]);
                        Vector3 shipDisplay = GetDisplayPosition(beamOrigin, camPos, mapW, mapH);
                        float3 gemOff = ToroidalMapEcs.ShortestOffsetXZ(beamOrigin, gemTransforms[gi].Position, mapW, mapH);
                        Vector3 gemDisplay = shipDisplay + new Vector3(gemOff.x, 0f, gemOff.z);

                        float beamY = Mathf.Max(beamOrigin.y, gemTransforms[gi].Position.y) + heightAboveGem;
                        shipDisplay.y = beamY;
                        gemDisplay.y = beamY;

                        float extension = GemTractorBeamDeployTracker.GetExtensionProgress(ships[si].Index, gems[gi].Index);
                        float widthExpand = GemTractorBeamDeployTracker.GetWidthExpandProgress(ships[si].Index, gems[gi].Index);
                        Vector3 tipDisplay = Vector3.Lerp(shipDisplay, gemDisplay, extension);

                        if (extension < 1f - 0.0001f)
                        {
                            float extendVis = GemTractorBeamDeployTracker.IsInDeployAnimation(ships[si].Index, gems[gi].Index)
                                ? 1f
                                : Mathf.Max(beamVisibility, 0.85f);
                            Color extendColor = new Color(teamBase.r, teamBase.g, teamBase.b, alphaExtendLine * extendVis);
                            DrawExtendLineWithWraps(shipDisplay, tipDisplay, mapW, mapH, extendColor);
                            continue;
                        }

                        float widthAtGem = GetSmoothedGemVisualDiameter(gems[gi], gemStates[gi]) *
                                           gemEndWidthScale * widthPulse;
                        float thinWidth = Mathf.Max(extendLineThickness, GemTractorBeamDeployTracker.ExtendLineThickness);
                        float currentWidth = Mathf.Lerp(thinWidth, widthAtGem, widthExpand);

                        Color colorShip = new Color(teamBase.r, teamBase.g, teamBase.b, pulsedAlphaAtShip * beamVisibility);
                        Color colorGem = new Color(teamBase.r, teamBase.g, teamBase.b, alphaAtGem * beamVisibility);
                        DrawConeBeamWithWraps(shipDisplay, gemDisplay, mapW, mapH, currentWidth, colorShip, colorGem);
                    }
                }
            }
        }

        static Vector3 GetDisplayPosition(float3 logicalPos, Vector3 cameraPos, float mapW, float mapH)
        {
            float dx = cameraPos.x - logicalPos.x;
            float dz = cameraPos.z - logicalPos.z;
            int k = (int)Mathf.Round(dx / mapW);
            int m = (int)Mathf.Round(dz / mapH);
            return new Vector3(logicalPos.x + k * mapW, logicalPos.y, logicalPos.z + m * mapH);
        }

        static bool IsGameplayCamera(Camera cam)
        {
            if (cam.cameraType != CameraType.Game)
                return false;
            if (cam.targetTexture != null)
                return false;
            return true;
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

            Draw.LineGeometry = LineGeometry.Volumetric3D;
            Draw.Line(origin, tip, thickness, LineEndCap.Round, color);
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

            if (beamRoundness > 0.001f)
                Draw.Triangle(shipDisplay, gemLeft, gemRight, beamRoundness, colorShip, colorGem, colorGem);
            else
                Draw.Triangle(shipDisplay, gemLeft, gemRight, colorShip, colorGem, colorGem);
        }

        static float GetSmoothedGemVisualDiameter(Entity gemEntity, in GemState gemState)
        {
            float raw = GemVisualDiameterRegistry.TryGetDiameter(gemEntity, out float registered)
                ? registered
                : GemVisualApplier.ComputeVisualDiameter(math.max(0.25f, gemState.Value));

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
    }
}
