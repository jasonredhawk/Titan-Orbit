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

            if (gemCounter != null)
                gemCounter.text = $"Gems: {playerShip.CurrentGems:F0}/{playerShip.GemCapacity:F0}";

            if (populationCounter != null)
                populationCounter.text = $"People: {playerShip.CurrentPeople:F0}/{playerShip.PeopleCapacity:F0}";

            if (shipLevelText != null)
                shipLevelText.text = $"Level {playerShip.ShipLevel}";

            if (shipTypeText != null)
                shipTypeText.text = playerShip.FocusType.ToString();

            if (teamIndicator != null)
                teamIndicator.color = GetTeamColor(playerShip.ShipTeam);

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
