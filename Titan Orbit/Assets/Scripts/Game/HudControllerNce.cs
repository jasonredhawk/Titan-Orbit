using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Lightweight NCE gameplay HUD: local ship HP/gems and match timer from ECS singletons.
    /// Runs on the gameplay canvas enabled by <see cref="NceGameFlowController"/> after team spawn.
    /// Client only — reads <see cref="EcsGameBridge"/> each frame; does not send RPCs or drive sim.
    /// </summary>
    public class HudControllerNce : MonoBehaviour
    {
        /// <summary>Text field for current/max hull points on the local ship ghost.</summary>
        [SerializeField] TMP_Text healthText;
        /// <summary>Text field for gem cargo and optional orbit/planet gem context.</summary>
        [SerializeField] TMP_Text gemsText;
        /// <summary>Text field for authoritative match countdown from <see cref="MatchStateSingleton"/>.</summary>
        [SerializeField] TMP_Text timerText;

        /// <summary>
        /// [UNITY] Per-frame HUD refresh — ship stats from local ghost; timer from client or host world.
        /// </summary>
        void Update()
        {
            // --- Local ship stats ---
            // [HYBRID] EcsGameBridge copies predicted/authoritative ship state for UI only.
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                if (healthText != null)
                    healthText.text = $"HP {ship.Health:0}/{ship.MaxHealth:0}";
                if (gemsText != null)
                {
                    string gems = $"Gems {ship.CurrentGems:0}/{ship.GemCapacity:0}";
                    // [TITAN-ORBIT] Orbit motor shows planet gem pool when ship is in ring deposit mode.
                    if (EcsGameBridge.TryGetLocalShipOrbitState(out var orbit) && orbit.UsingOrbitMotor)
                    {
                        gems += "  •  Orbiting";
                        if (EcsGameBridge.TryGetPlanetStateByPlanetId(orbit.OrbitPlanetId, out var planet))
                        {
                            float max = PlanetEconomyMath.GetMaxGemsForLevel(planet.PlanetLevel);
                            gems += $"  •  Planet L{planet.PlanetLevel} {planet.CurrentGems:0}/{max:0}";
                        }
                    }
                    gemsText.text = gems;
                }
            }

            // --- Match timer ---
            // [ECS/DOTS] MatchStateSingleton replicates from server; host reads ServerWorld, client reads ClientWorld.
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
