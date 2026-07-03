using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Shows/hides the moon orbit station menu when the local ship lands on a friendly gem moon.</summary>
    [DefaultExecutionOrder(50)]
    public class MoonOrbitStationController : MonoBehaviour
    {
        const float MenuDelayAfterLandingSeconds = 0.5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<MoonOrbitStationController>() != null)
                return;
            var go = new GameObject("MoonOrbitStationController");
            DontDestroyOnLoad(go);
            go.AddComponent<MoonOrbitStationController>();
        }

        OrbitStationUI _ui;
        float _landingCompleteTime = -1f;
        bool _menuVisible;

        void Update()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                HideMenu();
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipState(out var ship) ||
                ship.Team == TeamId.None ||
                ship.AwaitingTeamSelection ||
                ship.IsDead)
            {
                HideMenu();
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipMoonDockState(out var moonDock) ||
                moonDock.MoonPlanetId == 0 ||
                moonDock.LandingProgress < GemEconomyConstants.MoonLandingCompleteThreshold)
            {
                _landingCompleteTime = -1f;
                HideMenu();
                return;
            }

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(moonDock.MoonPlanetId, out var planet) ||
                planet.Ownership != ship.Team)
            {
                HideMenu();
                return;
            }

            if (EcsGameBridge.TryGetLocalShipInput(out var input) && input.Thrust)
            {
                HideMenu();
                return;
            }

            if (_landingCompleteTime < 0f)
            {
                _landingCompleteTime = Time.time;
                bool autoDeposit = PlayerPrefs.GetInt(
                    OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey,
                    OrbitDockSidebarPanelUI.AutoDepositGemsDefaultEnabled) != 0;
                MoonOrbitRpcClient.SetWantDepositGems(autoDeposit);
            }

            bool shouldShow = Time.time >= _landingCompleteTime + MenuDelayAfterLandingSeconds;
            if (shouldShow && !_menuVisible)
            {
                int homePlanetId = FindHomePlanetId(ship.Team);
                GetOrCreateUi().ShowFromEcs(moonDock.MoonPlanetId, homePlanetId);
                _menuVisible = true;
            }
            else if (!shouldShow && _menuVisible)
            {
                HideMenu();
            }
        }

        void HideMenu()
        {
            if (!_menuVisible)
                return;
            if (_ui != null)
                _ui.Hide();
            _menuVisible = false;
        }

        OrbitStationUI GetOrCreateUi()
        {
            if (_ui == null)
                _ui = OrbitStationUI.GetOrCreate();
            return _ui;
        }

        static int FindHomePlanetId(TeamId team)
        {
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return 0;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Ownership == team)
                    return states[i].PlanetId;
            }

            return 0;
        }
    }
}
