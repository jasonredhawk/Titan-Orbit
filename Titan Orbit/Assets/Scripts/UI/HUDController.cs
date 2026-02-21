using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Main HUD: ship stats (top-left), home planet stats (top-right). Gaming-style layout.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        [Header("Ship Stats (Top-Left)")]
        [SerializeField] private Slider healthBar;
        [SerializeField] private TextMeshProUGUI healthText;
        [SerializeField] private Slider gemBar;
        [SerializeField] private TextMeshProUGUI gemCounter;
        [SerializeField] private Slider peopleBar;
        [SerializeField] private TextMeshProUGUI populationCounter;
        [SerializeField] private TextMeshProUGUI shipLevelText;
        [SerializeField] private TextMeshProUGUI shipTypeText;
        [SerializeField] private Image teamIndicator;

        [Header("Home Planet Stats (Top-Right)")]
        [SerializeField] private GameObject homePlanetPanel;
        [SerializeField] private TextMeshProUGUI homePlanetLevelText;
        [SerializeField] private Slider homePlanetGemBar;
        [SerializeField] private TextMeshProUGUI homePlanetGemsText;

        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = Color.red;
        [SerializeField] private Color teamBColor = Color.blue;
        [SerializeField] private Color teamCColor = Color.green;

        [Header("Proximity Radar (planets around ship)")]
        [SerializeField] private GameObject proximityRadar;
        [SerializeField] private KeyCode proximityRadarToggleKey = KeyCode.R;

        private Starship playerShip;

        private void Update()
        {
            // Toggle proximity radar (when off, GameObject is inactive so no Update or rendering)
            if (proximityRadar == null)
                proximityRadar = transform.Find("ProximityRadar")?.gameObject;
            if (proximityRadar != null && UnityEngine.Input.GetKeyDown(proximityRadarToggleKey))
                proximityRadar.SetActive(!proximityRadar.activeSelf);

            if (playerShip == null)
            {
                foreach (var ship in FindObjectsOfType<Starship>())
                {
                    if (ship.IsOwner) { playerShip = ship; break; }
                }
                if (playerShip == null) return;
            }
            if (playerShip.IsDead) return;
            UpdateHUD();
        }

        private TextMeshProUGUI fallbackText;

        private void UpdateHUD()
        {
            if (healthBar != null)
                healthBar.value = playerShip.CurrentHealth / playerShip.MaxHealth;

            if (healthText != null)
                healthText.text = $"{playerShip.CurrentHealth:F0}/{playerShip.MaxHealth:F0}";

            if (gemBar != null)
                gemBar.value = playerShip.GemCapacity > 0 ? playerShip.CurrentGems / playerShip.GemCapacity : 0f;

            if (gemCounter != null)
                gemCounter.text = $"Gems: {playerShip.CurrentGems:F0}/{playerShip.GemCapacity:F0}";

            if (peopleBar != null)
                peopleBar.value = playerShip.PeopleCapacity > 0 ? playerShip.CurrentPeople / playerShip.PeopleCapacity : 0f;

            if (populationCounter != null)
                populationCounter.text = $"People: {playerShip.CurrentPeople:F0}/{playerShip.PeopleCapacity:F0}";

            if (shipLevelText != null)
                shipLevelText.text = $"Level {playerShip.ShipLevel}";

            if (shipTypeText != null)
                shipTypeText.text = playerShip.FocusType.ToString();

            if (teamIndicator != null)
                teamIndicator.color = GetTeamColor(playerShip.ShipTeam);

            // Home planet stats (top-right): show player's team base
            UpdateHomePlanetPanel();

            // Fallback: single combined text when individual elements not assigned
            if (healthText == null && gemCounter == null && populationCounter == null)
            {
                if (fallbackText == null) fallbackText = GetComponentInChildren<TextMeshProUGUI>();
                if (fallbackText != null)
                {
                    fallbackText.text = $"Health: {playerShip.CurrentHealth:F0}/{playerShip.MaxHealth:F0}\n" +
                        $"Gems: {playerShip.CurrentGems:F0}/{playerShip.GemCapacity:F0}\n" +
                        $"People: {playerShip.CurrentPeople:F0}/{playerShip.PeopleCapacity:F0}";
                }
            }
        }

        private void UpdateHomePlanetPanel()
        {
            HomePlanet homePlanet = GetHomePlanetForTeam(playerShip.ShipTeam);
            if (homePlanetPanel != null)
                homePlanetPanel.SetActive(homePlanet != null);
            if (homePlanet == null)
            {
                if (homePlanetLevelText != null) homePlanetLevelText.text = "—";
                if (homePlanetGemsText != null) homePlanetGemsText.text = "—";
                return;
            }
            if (homePlanetLevelText != null)
                homePlanetLevelText.text = $"Level {homePlanet.HomePlanetLevel}";
            
            if (homePlanetGemBar != null)
                homePlanetGemBar.value = homePlanet.MaxGems > 0 ? homePlanet.CurrentGems / homePlanet.MaxGems : 0f;
            
            if (homePlanetGemsText != null)
                homePlanetGemsText.text = $"{homePlanet.CurrentGems:F0} / {homePlanet.MaxGems:F0}";
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                if (hp.AssignedTeam == team) return hp;
            }
            return null;
        }

        private Color GetTeamColor(Core.TeamManager.Team team)
        {
            switch (team)
            {
                case Core.TeamManager.Team.TeamA:
                    return teamAColor;
                case Core.TeamManager.Team.TeamB:
                    return teamBColor;
                case Core.TeamManager.Team.TeamC:
                    return teamCColor;
                default:
                    return Color.white;
            }
        }
    }
}
