using TitanOrbit.Core;
using TitanOrbit.ECS;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.Game
{
    public class HudControllerNce : MonoBehaviour
    {
        [SerializeField] TMP_Text healthText;
        [SerializeField] TMP_Text gemsText;
        [SerializeField] TMP_Text timerText;

        void Update()
        {
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                if (healthText != null)
                    healthText.text = $"HP {ship.Health:0}/{ship.MaxHealth:0}";
                if (gemsText != null)
                {
                    string gems = $"Gems {ship.CurrentGems:0}/{ship.GemCapacity:0}";
                    if (EcsGameBridge.TryGetLocalShipOrbitState(out var orbit) && orbit.UsingOrbitMotor)
                        gems += "  •  Orbiting";
                    gemsText.text = gems;
                }
            }

            var world = EcsGameBridge.ClientWorld ?? EcsGameBridge.ServerWorld;
            if (world != null && world.IsCreated && timerText != null)
            {
                if (world.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton))
                    .TryGetSingleton<MatchStateSingleton>(out var match))
                    timerText.text = $"Time {match.MatchTimer:0}s";
            }
        }
    }
}
