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
    /// <para>
    /// [TITAN-ORBIT] Deposit intent stays on while truly docked. Failed ECS reads and brief
    /// <c>LandingProgress</c> dips use hysteresis — they must not call <see cref="HideMenuImmediate"/>
    /// (that cleared <see cref="MoonOrbitClientState.WantDepositGems"/> and silenced server deposits
    /// + SFX in both Editor and Windows).
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class MoonOrbitStationController : MonoBehaviour
    {
        /// <summary>Seconds after landing completes before the orbit menu appears (cinematic pause).</summary>
        const float MenuDelayAfterLandingSeconds = 0.5f;

        /// <summary>
        /// Consecutive bad dock frames required before hide while a dock session is active.
        /// Prevents one-frame LandingProgress / planet-cache gaps from killing deposit.
        /// </summary>
        const float UndockHysteresisSeconds = 0.75f;

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
        /// When &gt; 0, first observed soft-undock / read-fail at this <see cref="Time.time"/>.
        /// Hard leave (thrust / dead) bypasses hysteresis.
        /// </summary>
        float _undockPendingSince = -1f;

        /// <summary>Last known docked moon planet id while this dock session is active.</summary>
        int _latchedMoonPlanetId;

        /// <summary>Last known home planet id for Bank RPC while this dock session is active.</summary>
        int _latchedHomePlanetId;

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

            // --- Hard leave: thrust always undocks ---
            if (EcsGameBridge.TryGetLocalShipInput(out var input) && input.Thrust)
            {
                HideMenuImmediate();
                return;
            }

            // --- Resolve living local ship (tagged path works during GhostSpawnBacklog) ---
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                // Read miss — keep deposit while a dock session is active.
                if (_menuVisible || _landingCompleteTime >= 0f)
                {
                    MaybeHideAfterHysteresis();
                    return;
                }

                return;
            }

            if (ship.Team == TeamId.None || ship.AwaitingTeamSelection || ship.IsDead)
            {
                HideMenuImmediate();
                return;
            }

            // --- Moon dock component ---
            if (!EcsGameBridge.TryGetLocalShipMoonDockState(out var moonDock))
            {
                if (_menuVisible || _landingCompleteTime >= 0f)
                {
                    MaybeHideAfterHysteresis();
                    return;
                }

                return;
            }

            // Hard undock: moon cleared.
            if (moonDock.MoonPlanetId == 0)
            {
                HideMenuImmediate();
                return;
            }

            // Soft undock: landing progress dipped — hysteresis while session active.
            if (moonDock.LandingProgress < GemEconomyConstants.MoonLandingCompleteThreshold)
            {
                if (_menuVisible || _landingCompleteTime >= 0f)
                {
                    MaybeHideAfterHysteresis();
                    return;
                }

                return;
            }

            _latchedMoonPlanetId = moonDock.MoonPlanetId;

            // --- Friendly ownership ---
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(moonDock.MoonPlanetId, out var planet))
            {
                if (_menuVisible || _landingCompleteTime >= 0f)
                {
                    MaybeHideAfterHysteresis();
                    return;
                }

                return;
            }

            if (planet.Ownership != ship.Team)
            {
                HideMenuImmediate();
                return;
            }

            // Still docked — cancel pending soft-undock.
            _undockPendingSince = -1f;

            // --- First frame of completed landing: start timer + apply auto-deposit once ---
            if (_landingCompleteTime < 0f)
            {
                _landingCompleteTime = Time.time;
                bool autoDeposit = PlayerPrefs.GetInt(
                    OrbitDockSidebarPanelUI.AutoDepositGemsPrefsKey,
                    OrbitDockSidebarPanelUI.AutoDepositGemsDefaultEnabled) != 0;
                MoonOrbitRpcClient.SetWantDepositGems(autoDeposit);
                GetOrCreateUi();
            }

            bool shouldShow = Time.time >= _landingCompleteTime + MenuDelayAfterLandingSeconds;
            if (shouldShow && !_menuVisible)
            {
                int homePlanetId = ResolveHomePlanetId(ship.Team, planet, moonDock.MoonPlanetId);
                _latchedHomePlanetId = homePlanetId;
                GetOrCreateUi().ShowFromEcs(moonDock.MoonPlanetId, homePlanetId);
                _menuVisible = true;
            }
            else if (!shouldShow && _menuVisible)
            {
                HideMenuImmediate();
            }
        }

        /// <summary>
        /// Starts/continues soft-undock hysteresis. Only hides after
        /// <see cref="UndockHysteresisSeconds"/> of consecutive bad dock frames.
        /// </summary>
        void MaybeHideAfterHysteresis()
        {
            if (_landingCompleteTime < 0f && !_menuVisible)
                return;

            if (_undockPendingSince < 0f)
                _undockPendingSince = Time.time;

            if (Time.time - _undockPendingSince < UndockHysteresisSeconds)
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
            _undockPendingSince = -1f;
            MoonOrbitRpcClient.SetWantDepositGems(false);
            _landingCompleteTime = -1f;
            _latchedMoonPlanetId = 0;
            _latchedHomePlanetId = 0;
            if (!_menuVisible)
                return;
            if (_ui != null)
                _ui.Hide();
            _menuVisible = false;
        }

        /// <summary>Returns the cached <see cref="OrbitStationUI"/>, creating it on first use.</summary>
        OrbitStationUI GetOrCreateUi()
        {
            if (_ui == null)
                _ui = OrbitStationUI.GetOrCreate();
            return _ui;
        }

        /// <summary>
        /// Resolves the team's home planet id for Bank / store spending.
        /// Prefers <see cref="EcsGameBridge.TryGetHomePlanetIdForTeam"/> (replicated IsHomePlanet);
        /// if that fails while docked on the home moon, uses the docked planet id as a fallback.
        /// </summary>
        static int ResolveHomePlanetId(TeamId team, in PlanetState dockedPlanet, int dockedPlanetId)
        {
            // [TITAN-ORBIT] HomePlanetTag is server-only — querying it on the client always returned 0.
            if (EcsGameBridge.TryGetHomePlanetIdForTeam(team, out int homePlanetId) && homePlanetId > 0)
                return homePlanetId;

            if (dockedPlanet.IsHomePlanet && dockedPlanetId > 0)
                return dockedPlanetId;

            return 0;
        }
    }
}
