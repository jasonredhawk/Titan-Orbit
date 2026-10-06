using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
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

        World _matchWorld;
        EntityQuery _matchQuery;
        bool _matchQueryReady;
        int _shownHp = int.MinValue;
        int _shownHpMax = int.MinValue;
        int _shownGems = int.MinValue;
        int _shownGemCap = int.MinValue;
        int _shownTimer = int.MinValue;
        int _shownPlanetLevel;
        int _shownPlanetGems;

        void OnDestroy()
        {
            if (_matchQueryReady && _matchWorld != null && _matchWorld.IsCreated)
                _matchQuery.Dispose();
            _matchQueryReady = false;
            _matchWorld = null;
        }

        /// <summary>
        /// [UNITY] Per-frame HUD refresh — ship stats from local ghost; timer from client or host world.
        /// </summary>
        void Update()
        {
            // --- Local ship stats ---
            // [HYBRID] EcsGameBridge copies predicted/authoritative ship state for UI only.
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                int hp = Mathf.RoundToInt(ship.Health);
                int hpMax = Mathf.RoundToInt(ship.MaxHealth);
                if (healthText != null && (hp != _shownHp || hpMax != _shownHpMax))
                {
                    _shownHp = hp;
                    _shownHpMax = hpMax;
                    healthText.text = "HP " + hp + "/" + hpMax;
                }

                if (gemsText != null)
                {
                    int gems = Mathf.RoundToInt(ship.CurrentGems);
                    int gemCap = Mathf.RoundToInt(ship.GemCapacity);
                    bool orbiting = EcsGameBridge.TryGetLocalShipOrbitState(out var orbit) && orbit.UsingOrbitMotor;
                    int planetLevel = 0;
                    int planetGems = 0;
                    int planetMax = 0;
                    if (orbiting && EcsGameBridge.TryGetPlanetStateByPlanetId(orbit.OrbitPlanetId, out var planet))
                    {
                        planetLevel = planet.PlanetLevel;
                        planetGems = Mathf.RoundToInt(planet.CurrentGems);
                        planetMax = Mathf.RoundToInt(PlanetEconomyMath.GetMaxGemsForLevel(planet.PlanetLevel));
                    }

                    if (gems != _shownGems || gemCap != _shownGemCap || planetLevel != _shownPlanetLevel ||
                        planetGems != _shownPlanetGems)
                    {
                        _shownGems = gems;
                        _shownGemCap = gemCap;
                        _shownPlanetLevel = planetLevel;
                        _shownPlanetGems = planetGems;
                        string line = "Gems " + gems + "/" + gemCap;
                        if (orbiting)
                        {
                            line += "  •  Orbiting";
                            if (planetLevel > 0)
                                line += "  •  Planet L" + planetLevel + " " + planetGems + "/" + planetMax;
                        }

                        gemsText.text = line;
                    }
                }
            }

            // --- Match timer ---
            // [ECS/DOTS] MatchStateSingleton replicates from server; host reads ServerWorld, client reads ClientWorld.
            var world = EcsGameBridge.ClientWorld ?? EcsGameBridge.ServerWorld;
            if (world != null && world.IsCreated && timerText != null && TryGetMatchTimer(world, out int seconds) &&
                seconds != _shownTimer)
            {
                _shownTimer = seconds;
                timerText.text = "Time " + seconds + "s";
            }
        }

        bool TryGetMatchTimer(World world, out int seconds)
        {
            seconds = 0;
            if (_matchWorld != world)
            {
                if (_matchQueryReady && _matchWorld != null && _matchWorld.IsCreated)
                    _matchQuery.Dispose();
                _matchQuery = world.EntityManager.CreateEntityQuery(typeof(MatchStateSingleton));
                _matchWorld = world;
                _matchQueryReady = true;
            }

            if (!_matchQuery.TryGetSingleton<MatchStateSingleton>(out var match))
                return false;
            seconds = Mathf.FloorToInt(match.MatchTimer);
            return true;
        }
    }
}
