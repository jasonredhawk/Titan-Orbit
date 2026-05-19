using UnityEngine;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Draws thin Shapes lines from ships to gems only while they are actively moving toward the ship (tractor pull).
    /// </summary>
    [ExecuteAlways]
    public class GemTractorBeamVisual : ImmediateModeShapeDrawer
    {
        private static GemTractorBeamVisual instance;

        [Header("Beam")]
        [SerializeField] private float heightAboveGem = 0.28f;
        [SerializeField] private float lineThickness = 0.032f;
        [SerializeField] private float pulseThicknessAmplitude = 0.006f;
        [SerializeField] private float pulseSpeed = 8f;
        [SerializeField] private float alphaAtShip = 0.72f;
        [SerializeField] private float alphaAtGem = 0.16f;
        [SerializeField] private Color bonusBeamTint = new Color(1f, 0.92f, 0.35f, 1f);
        [Tooltip("Skip scene view / reflection cameras (same as other world Shapes drawers).")]
        [SerializeField] private bool gameplayCamerasOnly = true;

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
            base.OnDisable();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            GemTractorBeamMotionTracker.LateUpdateTick();
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
            float pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseThicknessAmplitude;
            float thickness = lineThickness * pulse;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.LineGeometry = LineGeometry.Volumetric3D;
                Draw.BlendMode = ShapesBlendMode.Transparent;

                for (int si = 0; si < ships.Count; si++)
                {
                    Starship ship = ships[si];
                    if (!IsShipEligibleForBeam(ship))
                        continue;

                    Vector3 shipPos = GetWorldPosition(ship);
                    Vector3 shipDisplay = ToroidalMap.GetDisplayPosition(shipPos, camPos);

                    Color teamBase = ship.ShipTeam != TeamManager.Team.None
                        ? TeamManager.GetTeamColor(ship.ShipTeam)
                        : new Color(0.85f, 0.95f, 1f);

                    for (int gi = 0; gi < gems.Count; gi++)
                    {
                        Gem gem = gems[gi];
                        if (!IsGemEligibleForBeam(gem))
                            continue;
                        if (!GemTractorBeamSettings.ShouldShowTractorBeam(ship, gem))
                            continue;

                        Vector3 gemPos = GetWorldPosition(gem);
                        Vector2 gemOff = ToroidalMap.ShortestOffsetXZ(shipPos, gemPos);
                        Vector3 gemDisplay = shipDisplay + new Vector3(gemOff.x, 0f, gemOff.y);

                        float beamY = Mathf.Max(shipPos.y, gemPos.y) + heightAboveGem;
                        shipDisplay.y = beamY;
                        gemDisplay.y = beamY;

                        Color beamColor = gem.IsBonusGem
                            ? Color.Lerp(teamBase, bonusBeamTint, 0.55f)
                            : teamBase;
                        Color colorShip = new Color(beamColor.r, beamColor.g, beamColor.b, alphaAtShip);
                        Color colorGem = new Color(beamColor.r, beamColor.g, beamColor.b, alphaAtGem);

                        DrawBeamWithWraps(shipDisplay, gemDisplay, mapW, mapH, thickness, colorShip, colorGem);
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
            float thickness, Color colorShip, Color colorGem)
        {
            DrawBeamSegment(shipDisplay, gemDisplay, thickness, colorShip, colorGem);

            Vector3[] offsets = {
                new Vector3(mapW, 0f, 0f),
                new Vector3(-mapW, 0f, 0f),
                new Vector3(0f, 0f, mapH),
                new Vector3(0f, 0f, -mapH)
            };
            foreach (var off in offsets)
                DrawBeamSegment(shipDisplay + off, gemDisplay + off, thickness, colorShip, colorGem);
        }

        private static void DrawBeamSegment(Vector3 shipDisplay, Vector3 gemDisplay, float thickness,
            Color colorShip, Color colorGem)
        {
            Draw.Line(shipDisplay, gemDisplay, thickness, LineEndCap.Round, colorShip, colorGem);
        }
    }
}
