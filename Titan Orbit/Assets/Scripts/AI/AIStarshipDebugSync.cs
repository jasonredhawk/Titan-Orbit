using UnityEngine;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Server-side AI debug state for visualization. Must NOT be a NetworkBehaviour — adding
    /// NetworkBehaviours at spawn time breaks NGO prefab sync and can crash the editor/client.
    /// </summary>
    public class AIStarshipDebugSync : MonoBehaviour
    {
        private Vector3 targetPosition;
        private int stateEnum;

        public Vector3 TargetPosition => targetPosition;
        public int StateEnum => stateEnum;

        /// <summary>Server only (called from <see cref="AIStarshipController"/>).</summary>
        public void SetDebug(Vector3 target, int state)
        {
            targetPosition = target;
            stateEnum = state;
        }

        public static string StateNameFromEnum(int s)
        {
            switch (s)
            {
                case 0: return "Idle";
                case 1: return "MovingToTarget";
                case 2: return "ShootingAsteroid";
                case 3: return "CollectingGems";
                case 4: return "ReturningToHome";
                case 5: return "LoadingPeople";
                case 6: return "MovingToPlanet";
                case 7: return "UnloadingPeople";
                case 8: return "AttackingEnemy";
                case 9: return "LevelingUp";
                default: return "?";
            }
        }
    }
}
