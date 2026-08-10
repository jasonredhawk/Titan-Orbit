using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Shapes immediate-mode people-transfer orbit ring and decorative level bands around an ECS planet proxy.
    /// </summary>
    [ExecuteAlways]
    public class PlanetOrbitRingVisual : ImmediateModeShapeDrawer
    {
        [Header("Ring Layout")]
        [SerializeField] float tiltDegrees = PlanetOrbitMath.LevelBandsTiltDegrees;
        [SerializeField] float innerRadius = PlanetOrbitMath.LevelBandsInnerRadiusLocal;
        [SerializeField] float ringThickness = PlanetOrbitMath.LevelBandThicknessLocal;
        [SerializeField] float gapBetweenBands = PlanetOrbitMath.LevelBandGapLocal;

        [Header("Appearance")]
        [Range(0.2f, 1f)]
        [SerializeField] float ringOpacity = 0.6f;
        [SerializeField] float homeRingOpacityBoost = 0.1f;

        [Header("Orbit Ring Fill")]
        [SerializeField] bool drawOrbitZoneFill = true;
        [SerializeField] Color orbitZoneTint = new Color(0.5f, 0.7f, 0.95f);
        [Range(0f, 1f)]
        [SerializeField] float orbitZonePeakAlpha = 0.3f;
        [Tooltip("Local Y offset for the people-transfer ring. 0 = planet equator / cross-section.")]
        [SerializeField] float orbitZoneHeightBelowPlanet = 0f;

        Transform _planetRoot;
        float _planetSize = 1f;
        int _planetLevel = 1;
        int _planetId;
        TeamId _team = TeamId.None;
        bool _isHome;

        /// <summary>
        /// Caches draw parameters for <see cref="DrawShapes"/>. Called at proxy Instantiates and again
        /// from <see cref="WorldBodyVisualApplier.RefreshPlanetVisualAppearance"/> when ghosted
        /// planet level or ownership changes (gem deposit level-up).
        /// </summary>
        public void Configure(Transform planetRoot, float planetSize, int planetLevel, TeamId team, bool isHome, int planetId)
        {
            // --- Cache presentation fields (Shapes reads these each DrawShapes) ---
            // [HYBRID] Not a live ECS query — EcsWorldVisualizer must re-Configure after level-up.
            _planetRoot = planetRoot;
            _planetSize = Mathf.Max(0.25f, planetSize);
            _planetLevel = ClampLevel(planetLevel);
            _team = team;
            _isHome = isHome;
            _planetId = planetId;
        }

        static int ClampLevel(int level) => Mathf.Clamp(level, 1, PlanetEconomyMath.MaxPlanetLevel);

        float GetInnerRadiusLocal()
        {
            PlanetOrbitMath.GetRingRadiiWorld(_planetSize, _planetLevel, out float inner, out _, out _);
            return inner / _planetSize;
        }

        float GetOuterRadiusLocal()
        {
            PlanetOrbitMath.GetRingRadiiWorld(_planetSize, _planetLevel, out _, out float outer, out _);
            return outer / _planetSize;
        }

        /// <summary>
        /// Skip Shapes work when the planet is far from the camera (presentation only).
        /// Matches pad-zone cull distance so nearby combat stays rich without drawing the whole map.
        /// </summary>
        const float MaxDrawDistance = 90f;

        /// <summary>
        /// [HYBRID] Soft orbit fill + decorative level bands for this planet proxy.
        /// Distance-culled so map-wide planets do not each pay a full Shapes pass.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (_planetRoot == null)
                _planetRoot = transform.parent;
            if (_planetRoot == null)
                return;

            // --- Distance cull ---
            // [TITAN-ORBIT] Each planet used to draw 32+ rings every camera pass map-wide.
            if (cam != null)
            {
                float maxDistSq = MaxDrawDistance * MaxDrawDistance;
                if ((_planetRoot.position - cam.transform.position).sqrMagnitude > maxDistSq)
                    return;
            }

            float innerLocal = GetInnerRadiusLocal();
            float outerLocal = GetOuterRadiusLocal();
            if (outerLocal - innerLocal < 0.02f)
                return;

            int ringCount = ClampLevel(_planetLevel);
            Matrix4x4 planetMatrix = _planetRoot.localToWorldMatrix;

            if (drawOrbitZoneFill)
            {
                Quaternion flatXZ = Quaternion.Euler(-90f, 0f, 0f);
                Vector3 offsetBelow = new Vector3(0f, -orbitZoneHeightBelowPlanet, 0f);
                Matrix4x4 zoneMatrix = planetMatrix * Matrix4x4.Translate(offsetBelow) * Matrix4x4.Rotate(flatXZ);
                PlanetRingMeshBuilder.DrawShapesOrbitRing(cam, zoneMatrix, innerLocal, outerLocal, orbitZoneTint, orbitZonePeakAlpha);
            }

            Color baseColor = _team != TeamId.None ? _team.ToColor() : new Color(0.75f, 0.75f, 0.8f);
            float opacity = ringOpacity + (_isHome ? homeRingOpacityBoost : 0f);
            Quaternion tilt = Quaternion.Euler(tiltDegrees, 0f, 0f); // matches PlanetOrbitMath.GetLevelBandsTiltRotation()
            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.RadiusSpace = ThicknessSpace.Meters;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.DiscGeometry = DiscGeometry.Flat2D;
                Draw.Matrix = planetMatrix * Matrix4x4.TRS(Vector3.zero, tilt, Vector3.one);
                PlanetRingMeshBuilder.DrawSaturnStyleLevelBands(
                    innerRadius, ringThickness, gapBetweenBands, ringCount,
                    baseColor, opacity, _planetId != 0 ? _planetId : GetInstanceID());
            }
        }
    }
}
