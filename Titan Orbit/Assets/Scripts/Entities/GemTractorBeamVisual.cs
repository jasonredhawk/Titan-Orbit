using System.Collections.Generic;
using UnityEngine;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws filled cone-shaped tractor beams from ships to gems while they are magnetically pulled.
    /// </summary>
    [ExecuteAlways]
    public class GemTractorBeamVisual : ImmediateModeShapeDrawer
    {
        private static GemTractorBeamVisual instance;

        [Header("Beam")]
        [SerializeField] private float heightAboveGem = 0.28f;
        [SerializeField] private float pulseWidthAmplitude = 0.06f;
        [SerializeField] private float pulseAlphaAmplitude = 0.14f;
        [Tooltip("Radians per second for the breathing pulse (~2 = one full cycle every ~3s).")]
        [SerializeField] private float pulseSpeed = 2f;
        [SerializeField] private float alphaAtShip = 0.72f;
        [SerializeField] private float alphaAtGem = 0.16f;
        [SerializeField] private Color bonusBeamTint = new Color(1f, 0.92f, 0.35f, 1f);
        [Tooltip("Skip scene view / reflection cameras (same as other world Shapes drawers).")]
        [SerializeField] private bool gameplayCamerasOnly = true;
        [Tooltip("Softens the wide end of the beam (0 = sharp triangle, 1 = fully rounded).")]
        [SerializeField] [Range(0f, 1f)] private float beamRoundness = 0.08f;
        [Tooltip("Scales how wide the beam is at the gem relative to the gem's visual size.")]
        [SerializeField] private float gemEndWidthScale = 1.05f;

        private static readonly Dictionary<int, float> smoothedGemDiameterById = new Dictionary<int, float>(64);
        private const float GemDiameterSmoothing = 10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstanceExists()
        {
            if (instance != null)
                return;
            instance = FindAnyObjectByType<GemTractorBeamVisual>();
            if (instance != null)
                return;

            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            if (go.GetComponent<GemTractorBeamVisual>() == null)
                instance = go.AddComponent<GemTractorBeamVisual>();
        }

        private void OnEnable()
        {
            instance = this;
            base.OnEnable();
        }

        private void OnDisable()
        {
            if (instance == this)
                instance = null;
            GemTractorBeamMotionTracker.Clear();
            GemTractorBeamVisibilityTracker.Clear();
            smoothedGemDiameterById.Clear();
            base.OnDisable();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            GemTractorBeamMotionTracker.LateUpdateTick();
            GemTractorBeamVisibilityTracker.LateUpdateTick();
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (cam == null)
                return;

            if (gameplayCamerasOnly && !IsGameplayCamera(cam))
                return;

            var ships = Starship.AllStarships;
            var gems = Gem.AllGems;
            if (ships == null || ships.Count == 0 || gems == null || gems.Count == 0)
                return;

            Vector3 camPos = cam.transform.position;
            float mapW = ToroidalMap.GetMapWidth();
            float mapH = ToroidalMap.GetMapHeight();
            // Slow breathing pulse: remap sin to 0..1, then ease so peaks/troughs linger briefly.
            float pulseWave = Mathf.SmoothStep(0f, 1f, (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
            float pulsedAlphaAtShip = Mathf.Clamp01(alphaAtShip + (pulseWave * 2f - 1f) * pulseAlphaAmplitude);
            float widthPulse = 1f + (pulseWave * 2f - 1f) * pulseWidthAmplitude;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.LineGeometry = LineGeometry.Flat2D;
                Draw.BlendMode = ShapesBlendMode.Transparent;

                for (int si = 0; si < ships.Count; si++)
                {
                    Starship ship = ships[si];
                    if (!IsShipEligibleForBeam(ship))
                        continue;

                    Color teamBase = ship.ShipTeam != TeamManager.Team.None
                        ? TeamManager.GetTeamColor(ship.ShipTeam)
                        : new Color(0.85f, 0.95f, 1f);

                    for (int gi = 0; gi < gems.Count; gi++)
                    {
                        Gem gem = gems[gi];
                        if (!IsGemEligibleForBeam(gem))
                            continue;

                        float beamVisibility = GemTractorBeamVisibilityTracker.GetVisibility(ship, gem);
                        if (beamVisibility <= 0.001f)
                            continue;

                        Vector3 beamOrigin = GemTractorBeamSettings.GetBeamOrigin(ship, gem);
                        Vector3 shipDisplay = ToroidalMap.GetDisplayPosition(beamOrigin, camPos);

                        Vector3 gemPos = GetWorldPosition(gem);
                        Vector2 gemOff = ToroidalMap.ShortestOffsetXZ(beamOrigin, gemPos);
                        Vector3 gemDisplay = shipDisplay + new Vector3(gemOff.x, 0f, gemOff.y);

                        float beamY = Mathf.Max(beamOrigin.y, gemPos.y) + heightAboveGem;
                        shipDisplay.y = beamY;
                        gemDisplay.y = beamY;

                        Color beamColor = gem.IsBonusGem
                            ? Color.Lerp(teamBase, bonusBeamTint, 0.55f)
                            : teamBase;
                        Color colorShip = new Color(beamColor.r, beamColor.g, beamColor.b, pulsedAlphaAtShip * beamVisibility);
                        Color colorGem = new Color(beamColor.r, beamColor.g, beamColor.b, alphaAtGem * beamVisibility);

                        float widthAtGem = GetSmoothedGemVisualDiameter(gem) * gemEndWidthScale * widthPulse;
                        DrawBeamWithWraps(shipDisplay, gemDisplay, mapW, mapH, widthAtGem,
                            colorShip, colorGem);
                    }
                }
            }
        }

        private static bool IsGameplayCamera(UnityEngine.Camera cam)
        {
            if (cam.cameraType != CameraType.Game)
                return false;
            if (cam.targetTexture != null)
                return false;
            return true;
        }

        private static bool IsShipEligibleForBeam(Starship ship)
        {
            if (ship == null || !ship.IsSpawned || ship.IsDead)
                return false;
            if (ship.IsGemCollectionSuppressed || ship.GemMoonDocked)
                return false;
            if (ship.CurrentGems >= ship.GemCapacity)
                return false;
            return true;
        }

        private static bool IsGemEligibleForBeam(Gem gem)
        {
            if (gem == null || !gem.IsSpawned || gem.IsInPool || gem.IsDepositGem)
                return false;
            if (gem.Value <= 0f)
                return false;
            return true;
        }

        private static Vector3 GetWorldPosition(Component target)
        {
            var rb = target.GetComponent<Rigidbody>();
            return rb != null ? rb.position : target.transform.position;
        }

        private void DrawBeamWithWraps(Vector3 shipDisplay, Vector3 gemDisplay, float mapW, float mapH,
            float widthAtGem, Color colorShip, Color colorGem)
        {
            DrawConeBeamSegment(shipDisplay, gemDisplay, widthAtGem, colorShip, colorGem);

            Vector3[] offsets = {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH)
            };
            foreach (var off in offsets)
            {
                DrawConeBeamSegment(shipDisplay + off, gemDisplay + off, widthAtGem, colorShip, colorGem);
            }
        }

        private void DrawConeBeamSegment(Vector3 shipDisplay, Vector3 gemDisplay, float widthAtGem,
            Color colorShip, Color colorGem)
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

            // Apex at ship, wide base at gem — single filled triangle cone.
            if (beamRoundness > 0.001f)
            {
                Draw.Triangle(shipDisplay, gemLeft, gemRight, beamRoundness, colorShip, colorGem, colorGem);
                return;
            }

            Draw.Triangle(shipDisplay, gemLeft, gemRight, colorShip, colorGem, colorGem);
        }

        private static float GetSmoothedGemVisualDiameter(Gem gem)
        {
            if (gem == null)
                return 0f;

            int gemId = gem.GetInstanceID();
            float raw = GetGemVisualDiameter(gem);
            if (!smoothedGemDiameterById.TryGetValue(gemId, out float smoothed))
            {
                smoothedGemDiameterById[gemId] = raw;
                return raw;
            }

            smoothed = Mathf.Lerp(smoothed, raw, Time.deltaTime * GemDiameterSmoothing);
            smoothedGemDiameterById[gemId] = smoothed;
            return smoothed;
        }

        private static float GetGemVisualDiameter(Gem gem)
        {
            if (gem == null)
                return 0f;

            var renderer = gem.GetComponent<Renderer>();
            if (renderer != null)
            {
                Vector3 extents = renderer.bounds.extents;
                return Mathf.Max(extents.x, extents.z) * 2f;
            }

            return gem.transform.lossyScale.x;
        }
    }
}
