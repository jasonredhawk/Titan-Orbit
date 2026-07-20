using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Shows/hides the moon orbit station menu when the local ship lands on a friendly gem moon.
    /// Opens <see cref="OrbitStationUI"/> with the docked store planet and the team's home planet id
    /// (needed for Bank / contributed-gem RPC polls). Client-only presentation controller.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class MoonOrbitStationController : MonoBehaviour
    {
        /// <summary>Seconds after landing completes before the orbit menu appears (cinematic pause).</summary>
        const float MenuDelayAfterLandingSeconds = 0.5f;

        /// <summary>
        /// [UNITY] Ensures one controller exists after scene load so moon dock can open the store
        /// without a scene-placed prefab.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            // --- Ensure setup ---
            if (FindFirstObjectByType<MoonOrbitStationController>() != null)
                return;
            var go = new GameObject("MoonOrbitStationController");
            DontDestroyOnLoad(go);
            go.AddComponent<MoonOrbitStationController>();
        }

        /// <summary>Cached orbit UI instance (created on first dock).</summary>
        OrbitStationUI _ui;

        /// <summary>Time.time when landing first hit the complete threshold; -1 when not docked.</summary>
        float _landingCompleteTime = -1f;

        /// <summary>True while ShowFromEcs has opened the menu for this dock session.</summary>
        bool _menuVisible;

        /// <summary>
        /// Each frame: if the local ship is fully landed on a friendly moon (and not thrusting),
        /// open the orbit station after a short delay; otherwise hide and clear deposit intent.
        /// </summary>
        void Update()
        {
            // --- Per-frame dock / menu gate ---
            if (!EcsGameBridge.IsNetworkInGame())
            {
                HideMenu();
                return;
            }

            // --- Require a living local ship with a team ---
            if (!EcsGameBridge.TryGetLocalShipState(out var ship) ||
                ship.Team == TeamId.None ||
                ship.AwaitingTeamSelection ||
                ship.IsDead)
            {
                HideMenu();
                return;
            }

            // --- Require moon dock progress complete ---
            if (!EcsGameBridge.TryGetLocalShipMoonDockState(out var moonDock) ||
                moonDock.MoonPlanetId == 0 ||
                moonDock.LandingProgress < GemEconomyConstants.MoonLandingCompleteThreshold)
            {
                _landingCompleteTime = -1f;
                HideMenu();
                return;
            }

            // --- Friendly ownership only (enemy moons have no store) ---
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(moonDock.MoonPlanetId, out var planet) ||
                planet.Ownership != ship.Team)
            {
                HideMenu();
                return;
            }

            // --- Thrust undocks / dismisses the menu ---
            if (EcsGameBridge.TryGetLocalShipInput(out var input) && input.Thrust)
            {
                HideMenu();
                return;
            }

            // --- First frame of completed landing: start timer + apply auto-deposit preference ---
            if (_landingCompleteTime < 0f)
            {
                _landingCompleteTime = Time.time;
                bool autoDeposit = PlayerPrefs.GetInt(
                    OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey,
                    OrbitDockSidebarPanelUI.AutoDepositGemsDefaultEnabled) != 0;
                MoonOrbitRpcClient.SetWantDepositGems(autoDeposit);
                // Pre-create UI during the landing pause so opening the menu does not hitch the camera.
                GetOrCreateUi();
            }

            bool shouldShow = Time.time >= _landingCompleteTime + MenuDelayAfterLandingSeconds;
            if (shouldShow && !_menuVisible)
            {
                // Home planet id drives Bank (contributed gems) RPC — must be > 0 or sidebar stays 0.
                int homePlanetId = ResolveHomePlanetId(ship.Team, planet, moonDock.MoonPlanetId);
                GetOrCreateUi().ShowFromEcs(moonDock.MoonPlanetId, homePlanetId);
                _menuVisible = true;
            }
            else if (!shouldShow && _menuVisible)
            {
                HideMenu();
            }
        }

        /// <summary>
        /// Closes the orbit UI, clears the landing timer, and turns off deposit intent so gems
        /// stop transferring once the player leaves the dock.
        /// </summary>
        void HideMenu()
        {
            // --- HideMenu ---
            MoonOrbitRpcClient.SetWantDepositGems(false);
            _landingCompleteTime = -1f;
            if (!_menuVisible)
                return;
            if (_ui != null)
                _ui.Hide();
            _menuVisible = false;
        }

        /// <summary>Returns the cached <see cref="OrbitStationUI"/>, creating it on first use.</summary>
        OrbitStationUI GetOrCreateUi()
        {
            // --- Compute value ---
            if (_ui == null)
                _ui = OrbitStationUI.GetOrCreate();
            return _ui;
        }

        /// <summary>
        /// Resolves the team's home planet id for Bank / store spending.
        /// Prefers <see cref="EcsGameBridge.TryGetHomePlanetIdForTeam"/> (replicated IsHomePlanet);
        /// if that fails while docked on the home moon, uses the docked planet id as a fallback.
        /// </summary>
        /// <param name="team">Local ship team.</param>
        /// <param name="dockedPlanet">Planet state for the moon we are docked on.</param>
        /// <param name="dockedPlanetId">PlanetId of that docked planet.</param>
        /// <returns>Home planet id, or 0 if unknown (Bank cannot refresh).</returns>
        static int ResolveHomePlanetId(TeamId team, in PlanetState dockedPlanet, int dockedPlanetId)
        {
            // --- Primary: quarantine-safe bridge lookup (IsHomePlanet, not HomePlanetTag) ---
            // [TITAN-ORBIT] HomePlanetTag is server-only — querying it on the client always returned 0,
            // so RequestContributedGems never ran and the GEM DEPOSITS Bank stayed at 0.
            if (EcsGameBridge.TryGetHomePlanetIdForTeam(team, out int homePlanetId) && homePlanetId > 0)
                return homePlanetId;

            // --- Fallback: docked moon is already the home capital ---
            if (dockedPlanet.IsHomePlanet && dockedPlanetId > 0)
                return dockedPlanetId;

            return 0;
        }
    }
}
