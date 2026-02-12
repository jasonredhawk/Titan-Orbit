using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Main HUD controller displaying health, gems, population, ship level, etc.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        [Header("Health")]
        [SerializeField] private Slider healthBar;
        [SerializeField] private TextMeshProUGUI healthText;

        [Header("Resources")]
        [SerializeField] private TextMeshProUGUI gemCounter;
        [SerializeField] private TextMeshProUGUI populationCounter;

        [Header("Ship Info")]
        [SerializeField] private TextMeshProUGUI shipLevelText;
        [SerializeField] private TextMeshProUGUI shipTypeText;
        [SerializeField] private Image teamIndicator;

        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = Color.red;
        [SerializeField] private Color teamBColor = Color.blue;
        [SerializeField] private Color teamCColor = Color.green;

        private Starship playerShip;

        private void Update()
        {
            if (playerShip == null)
            {
                // Find player ship
                playerShip = FindObjectOfType<Starship>();
                if (playerShip == null) return;
            }

            UpdateHUD();
        }

        private void UpdateHUD()
        {
            // Update health
            if (healthBar != null)
            {
                healthBar.value = playerShip.CurrentHealth / playerShip.MaxHealth;
            }

            if (healthText != null)
            {
                healthText.text = $"{playerShip.CurrentHealth:F0}/{playerShip.MaxHealth:F0}";
            }

            // Update gems
            if (gemCounter != null)
            {
                gemCounter.text = $"Gems: {playerShip.CurrentGems:F0}/{playerShip.GemCapacity:F0}";
            }

            // Update population
            if (populationCounter != null)
            {
                populationCounter.text = $"People: {playerShip.CurrentPeople:F0}/{playerShip.PeopleCapacity:F0}";
            }

            // Update ship level
            if (shipLevelText != null)
            {
                shipLevelText.text = $"Level {playerShip.ShipLevel}";
            }

            // Update ship type
            if (shipTypeText != null)
            {
                shipTypeText.text = playerShip.FocusType.ToString();
            }

            // Update team indicator
            if (teamIndicator != null)
            {
                Color teamColor = GetTeamColor(playerShip.ShipTeam);
                teamIndicator.color = teamColor;
            }
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
