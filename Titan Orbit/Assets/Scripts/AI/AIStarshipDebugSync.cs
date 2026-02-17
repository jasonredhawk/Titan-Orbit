using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Syncs AI debug data (target position, state enum) from server to client for debug visualization.
    /// Only populated when DebugMode. Add to AI ships in AIStarshipManager.
    /// </summary>
    public class AIStarshipDebugSync : NetworkBehaviour
    {
        private NetworkVariable<Vector3> targetPosition = new NetworkVariable<Vector3>(Vector3.zero);
        private NetworkVariable<int> stateEnum = new NetworkVariable<int>(0);

        public Vector3 TargetPosition => targetPosition.Value;
        public int StateEnum => stateEnum.Value;

        public void SetDebug(Vector3 target, int state)
        {
            if (!IsServer) return;
            targetPosition.Value = target;
            stateEnum.Value = state;
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
                default: return "?";
            }
        }
    }
}
