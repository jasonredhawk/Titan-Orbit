using UnityEngine;
using Shapes;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Debug visualization for AI ships: line from ship to target, text above ship showing type and state.
    /// Only visible when GameManager.DebugMode is true. Add to a persistent GameObject (e.g. Systems).
    /// </summary>
    [ExecuteAlways]
    public class AIStarshipDebugVisualizer : ImmediateModeShapeDrawer
    {
        [Header("Debug Visual Settings")]
        [SerializeField] private float lineThickness = 0.15f;
        [SerializeField] private Color lineColor = new Color(1f, 0.5f, 0f, 0.8f);
        [SerializeField] private float textHeightAboveShip = 2f;
        [SerializeField] private float textSize = 8f;

        public override void DrawShapes(UnityEngine.Camera cam)
        {
            if (cam == null) return;
            if (GameManager.Instance == null || !GameManager.Instance.DebugMode) return;

            foreach (var ship in Object.FindObjectsOfType<Starship>())
            {
                if (ship == null || ship.IsDead) continue;
                var ai = ship.GetComponent<AIStarshipController>();
                if (ai == null) continue;

                Vector3 shipPos = ship.transform.position;
                shipPos.y = 0f;

                // Get debug data (synced from server via AIStarshipDebugSync if present)
                var sync = ship.GetComponent<AIStarshipDebugSync>();
                Vector3 targetPos = sync != null ? sync.TargetPosition : shipPos;
                string stateStr = sync != null ? AIStarshipDebugSync.StateNameFromEnum(sync.StateEnum) : "?";
                string typeStr = ai.BehaviorType == AIStarshipController.AIBehaviorType.Mining ? "Miner" : "Transporter";

                // Text above ship: "Miner: MovingToAsteroid" - small, billboard (face camera) so readable
                string label = $"{typeStr}: {stateStr}";
                Vector3 textPos = shipPos + Vector3.up * textHeightAboveShip;

                using (Draw.Command(cam))
                {
                    Draw.ResetAllDrawStates();
                    Draw.LineGeometry = LineGeometry.Flat2D;
                    Draw.ThicknessSpace = ThicknessSpace.Meters;

                    // Line from ship to target
                    Draw.Line(shipPos, targetPos, lineThickness, LineEndCap.Round, lineColor);

                    // Text label above ship - face camera (billboard) for visibility; no rotation with ship
                    Quaternion textRot = Quaternion.LookRotation(textPos - cam.transform.position);
                    Draw.Text(textPos, textRot, label, TextAlign.Center, textSize, new Color(1f, 1f, 1f, 0.95f));
                }
            }
        }
    }
}
