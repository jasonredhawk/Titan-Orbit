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
        /// When &gt; 0, we first observed a "should hide" condition at this <see cref="Time.time"/>.
        /// Brief ECS read gaps (GhostSpawnBacklog) must not clear deposit intent / close the menu.
        /// </summary>
        float _hidePendingSince = -1f;

        /// <summary>
        /// Seconds to tolerate failed local-ship / dock reads before actually hiding.
        /// [TITAN-ORBIT] HideMenu clears <see cref="MoonOrbitClientState.WantDepositGems"/> — a one-frame
        /// miss used to silence the deposit metronome and stop auto-deposit.
        /// </summary>
        const float HideGraceSeconds = 0.4f;

        /// <summary>
        /// Each frame: if the local ship is fully landed on a friendly moon (and not thrusting),
        /// open the orbit station after a short delay; otherwise hide and clear deposit intent.
        /// </summary>
        void Update()
        {
            // --- Per-frame dock / menu gate ---
            if (!EcsGameBridge.IsNetworkInGame())
            {
                HideMenuImmediate();
                return;
            }

            // --- Require a living local ship with a team ---
            if (!EcsGameBridge.TryGetLocalShipState(out var ship) ||
                ship.Team == TeamId.None ||
                ship.AwaitingTeamSelection ||
                ship.IsDead)
            {
                RequestHideMenu();
                return;
            }

            // --- Require moon dock progress complete ---
            if (!EcsGameBridge.TryGetLocalShipMoonDockState(out var moonDock) ||
                moonDock.MoonPlanetId == 0 ||
                moonDock.LandingProgress < GemEconomyConstants.MoonLandingCompleteThreshold)
            {
                RequestHideMenu();
                return;
            }

            // --- Friendly ownership only (enemy moons have no store) ---
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(moonDock.MoonPlanetId, out var planet) ||
                planet.Ownership != ship.Team)
            {
                RequestHideMenu();
                return;
            }

            // --- Thrust undocks / dismisses the menu (hard leave — no grace) ---
            if (EcsGameBridge.TryGetLocalShipInput(out var input) && input.Thrust)
            {
                HideMenuImmediate();
                return;
            }

            // Still docked — cancel any pending hide from a brief read gap.
            _hidePendingSince = -1f;

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
                HideMenuImmediate();
            }
        }

        /// <summary>
        /// Starts/continues a short grace timer before <see cref="HideMenuImmediate"/>.
        /// Ignores one-frame ECS lookup failures so deposit SFX and auto-deposit stay on.
        /// </summary>
        void RequestHideMenu()
        {
            // --- Grace before hide ---
            if (_landingCompleteTime < 0f && !_menuVisible)
            {
                // Never docked this session — nothing to clear.
                return;
            }

            if (_hidePendingSince < 0f)
                _hidePendingSince = Time.time;

            if (Time.time - _hidePendingSince < HideGraceSeconds)
                return;

            HideMenuImmediate();
        }

        /// <summary>
        /// Closes the orbit UI, clears the landing timer, and turns off deposit intent so gems
        /// stop transferring once the player leaves the dock.
        /// </summary>
        void HideMenuImmediate()
        {
            // --- HideMenuImmediate ---
            _hidePendingSince = -1f;
            MoonOrbitRpcClient.SetWantDepositGems(false);
            _landingCompleteTime = -1f;
            if (!_menuVisible)
                return;
            if (_ui != null)
                _ui.Hide();
            _menuVisible = false;
        }

        /// <summary>Legacy name kept for any external callers — same as immediate hide.</summary>
        void HideMenu() => HideMenuImmediate();

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
