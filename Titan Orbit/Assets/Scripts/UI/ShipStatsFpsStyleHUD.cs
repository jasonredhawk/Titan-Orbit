using UnityEngine;
using UnityEngine.Rendering;
using Unity.Netcode;
using TitanOrbit.Entities;
using Shapes;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Arc HUD centered on the ship: left = Health (outer), Energy (inner); right = Gems (outer), People (inner).
    /// Bars are thin and sized to surround the ship without taking too much screen space.
    /// </summary>
    public class ShipStatsFpsStyleHUD : ImmediateModeShapeDrawer
    {
        [Header("Arc bar style (gap between inner/outer ≈ bar thickness; outer arc just inside radar)")]
        [Range(0.3f, 2f)] [SerializeField] private float angularSpanRad = 1.1f;
        [Range(0.02f, 0.2f)] [SerializeField] private float outlineThickness = 0.08f;
        [Range(0.15f, 0.8f)] [SerializeField] private float barThickness = 0.4f;
        [Tooltip("Left outer arc. Set so outer edge stays clearly inside the proximity radar ring.")]
        [Range(4f, 14f)] [SerializeField] private float radiusHealth = 7.6f;
        [Tooltip("Left inner arc. Keep (radiusHealth - radiusEnergy) ≈ 2× barThickness for gap = bar thickness.")]
        [Range(3f, 13f)] [SerializeField] private float radiusEnergy = 6.8f;
        [Tooltip("Right outer arc. Match radiusHealth so radar sits just outside.")]
        [Range(4f, 14f)] [SerializeField] private float radiusGems = 7.6f;
        [Tooltip("Right inner arc. Keep (radiusGems - radiusPeople) ≈ 2× barThickness.")]
        [Range(3f, 13f)] [SerializeField] private float radiusPeople = 6.8f;

        [Header("Label style")]
        [Range(1f, 8f)] [SerializeField] private float labelFontSize = 3.2f;
        [Range(0.2f, 1.5f)] [SerializeField] private float labelOffsetFromArc = 0.5f;
        [Tooltip("Nudge so two max labels at same tip (left/right) don't overlap.")]
        [Range(0.1f, 0.8f)] [SerializeField] private float labelTipNudge = 0.35f;
        [SerializeField] private Color labelColor = new Color(1f, 1f, 1f, 0.95f);

        [Header("Colors")]
        [SerializeField] private Color healthColor = new Color(0.2f, 0.9f, 0.45f, 1f);   // green
        [SerializeField] private Color energyColor = new Color(0.2f, 0.65f, 0.95f, 1f);  // blue
        [SerializeField] private Color gemsColor = new Color(0.95f, 0.25f, 0.2f, 1f);    // red
        [SerializeField] private Color peopleColor = new Color(0.9f, 0.75f, 0.3f, 1f);  // amber
        [SerializeField] private Color outlineColor = new Color(0.4f, 0.45f, 0.55f, 0.9f);

        private Starship _playerShip;
        private UnityEngine.Camera _targetCamera;

        /// <summary>Returns the local player's ship only (via SpawnManager.GetLocalPlayerObject), not AI or other clients' ships.</summary>
        private Starship GetPlayerShip()
        {
            if (_playerShip != null && !_playerShip.IsDead) return _playerShip;
            _playerShip = null;
            if (NetworkManager.Singleton == null) return null;
            NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer == null) return null;
            var ship = localPlayer.GetComponent<Starship>();
            if (ship != null && !ship.IsDead) _playerShip = ship;
            return _playerShip;
        }

        private void Awake()
        {
            _targetCamera = GetComponentInParent<UnityEngine.Camera>();
            if (_targetCamera == null) _targetCamera = UnityEngine.Camera.main;
        }

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            // Only draw in our target camera (main / this object's camera)
            if (_targetCamera != null && cam != _targetCamera)
                return;
            // Skip on dedicated server (no local player to show HUD for)
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsClient)
                return;
            Starship ship = GetPlayerShip();
            if (ship == null)
                return;

            using (Draw.Command(cam))
            {
                Draw.ZTest = CompareFunction.Always;
                Draw.Matrix = transform.localToWorldMatrix;
                Draw.BlendMode = ShapesBlendMode.Transparent;
                Draw.LineGeometry = LineGeometry.Flat2D;

                // Center arcs on the ship (origin in our draw space = ship position in transform's local 2D plane)
                Vector3 shipLocal = transform.InverseTransformPoint(ship.transform.position);
                Vector2 origin = new Vector2(shipLocal.x, shipLocal.y);

                float half = angularSpanRad * 0.5f;
                // Left: 0 = right, TAU/2 = left (math CCW)
                float leftStart = ShapesMath.TAU / 2f - half;
                float leftEnd = ShapesMath.TAU / 2f + half;
                // Right: opposite side
                float rightStart = -half;
                float rightEnd = half;

                float healthMax = ship.MaxHealth;
                float energyCap = ship.EnergyCapacity;
                float gemCap = ship.GemCapacity;
                float peopleCap = ship.PeopleCapacity;
                float healthFill = healthMax > 0f ? Mathf.Clamp01(ship.CurrentHealth / healthMax) : 0f;
                float energyFill = energyCap > 0f ? Mathf.Clamp01(ship.CurrentEnergy / energyCap) : 0f;
                float gemsFill = gemCap > 0f ? Mathf.Clamp01(ship.CurrentGems / gemCap) : 0f;
                float peopleFill = peopleCap > 0f ? Mathf.Clamp01(ship.CurrentPeople / peopleCap) : 0f;

                // Left: Health (outer), Energy (inner)
                DrawArcBar(origin, radiusHealth, leftStart, leftEnd, healthFill, healthColor, reverseFill: false);
                DrawArcBar(origin, radiusEnergy, leftStart, leftEnd, energyFill, energyColor, reverseFill: false);
                // Right: Gems (outer), People (inner) – fill direction reversed so progress reads correctly
                DrawArcBar(origin, radiusGems, rightStart, rightEnd, gemsFill, gemsColor, reverseFill: true);
                DrawArcBar(origin, radiusPeople, rightStart, rightEnd, peopleFill, peopleColor, reverseFill: true);

                // Max value at the top tip of each arc (left tip = leftEnd, right tip = rightEnd)
                DrawArcLabel(origin, radiusHealth + labelOffsetFromArc, leftEnd, labelTipNudge, Mathf.FloorToInt(healthMax).ToString());
                DrawArcLabel(origin, radiusEnergy + labelOffsetFromArc, leftEnd, -labelTipNudge, Mathf.FloorToInt(energyCap).ToString());
                DrawArcLabel(origin, radiusGems + labelOffsetFromArc, rightEnd, labelTipNudge, Mathf.FloorToInt(gemCap).ToString());
                DrawArcLabel(origin, radiusPeople + labelOffsetFromArc, rightEnd, -labelTipNudge, Mathf.FloorToInt(peopleCap).ToString());
            }
        }

        private void DrawArcLabel(Vector2 origin, float radius, float tipAngRad, float perpendicularNudge, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Vector2 dir = ShapesMath.AngToDir(tipAngRad);
            Vector2 perp = new Vector2(-dir.y, dir.x);
            Vector2 pos = origin + dir * radius + perp * perpendicularNudge;
            Draw.FontSize = labelFontSize;
            Draw.Text(pos, Quaternion.identity, text, TextAlign.Center, labelColor);
        }

        private void DrawArcBar(Vector2 origin, float radius, float angStart, float angEnd, float fill01, Color fillColor, bool reverseFill = false)
        {
            fill01 = Mathf.Clamp01(fill01);
            float fillAng;
            float arcStart, arcEnd;
            if (reverseFill)
            {
                // Fill from angStart toward angEnd as 0 -> 1 (reversed for right-side bars)
                fillAng = Mathf.Lerp(angStart, angEnd, fill01);
                arcStart = angStart;
                arcEnd = fillAng;
            }
            else
            {
                // Fill from angEnd toward angStart as 0 -> 1
                fillAng = Mathf.Lerp(angEnd, angStart, fill01);
                arcStart = angEnd;
                arcEnd = fillAng;
            }

            // Filled arc segment (round caps at both ends)
            Draw.Arc(origin, radius, barThickness, arcStart, arcEnd, ArcEndCap.Round, fillColor);

            // Moving disc at current fill end (over the round cap)
            Vector2 endPos = origin + ShapesMath.AngToDir(fillAng) * radius;
            Draw.Disc(endPos, barThickness / 2f + outlineThickness / 2f, outlineColor);
            Draw.Disc(endPos, barThickness / 2f - outlineThickness / 2f, fillColor);

            // Rounded outline for full arc (round ends only)
            DrawRoundedArcOutline(origin, radius, barThickness, outlineThickness, angStart, angEnd);
        }

        private void DrawRoundedArcOutline(Vector2 origin, float radius, float thickness, float outlineThickness, float angStart, float angEnd)
        {
            float innerRadius = radius - thickness / 2f;
            float outerRadius = radius + thickness / 2f;
            // End arcs exactly at cap angles so they don't stick out past the rounded caps
            Draw.Arc(origin, innerRadius, outlineThickness, angStart, angEnd, ArcEndCap.Round, outlineColor);
            Draw.Arc(origin, outerRadius, outlineThickness, angStart, angEnd, ArcEndCap.Round, outlineColor);
            // Semicircle caps at each end so the bar ends read as round (FPS-style)
            Vector2 originStart = origin + ShapesMath.AngToDir(angStart) * radius;
            Vector2 originEnd = origin + ShapesMath.AngToDir(angEnd) * radius;
            Draw.Arc(originStart, thickness / 2f, outlineThickness, angStart, angStart - ShapesMath.TAU / 2f, ArcEndCap.Round, outlineColor);
            Draw.Arc(originEnd, thickness / 2f, outlineThickness, angEnd, angEnd + ShapesMath.TAU / 2f, ArcEndCap.Round, outlineColor);
        }
    }
}
