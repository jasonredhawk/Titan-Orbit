using TitanOrbit.Core;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Pre-match team pick overlay: three buttons send <see cref="TeamId"/> choice to
    /// <see cref="TitanOrbitSessionManager.RequestTeam"/>. Shown when the player connects
    /// before a team is assigned; hides itself after a successful pick. Client-only UI —
    /// server validates team balance in team management systems.
    /// </summary>
    public class TeamSelectionController : MonoBehaviour
    {
        [SerializeField] Button teamAButton;
        [SerializeField] Button teamBButton;
        [SerializeField] Button teamCButton;

        /// <summary>Wires button clicks to <see cref="PickTeam"/> for each team slot.</summary>
        void Start()
        {
            // --- Unity lifecycle ---
            if (teamAButton != null) teamAButton.onClick.AddListener(() => PickTeam(TeamId.TeamA));
            if (teamBButton != null) teamBButton.onClick.AddListener(() => PickTeam(TeamId.TeamB));
            if (teamCButton != null) teamCButton.onClick.AddListener(() => PickTeam(TeamId.TeamC));
        }

        /// <summary>
        /// Sends team request to session manager and dismisses this overlay.
        /// </summary>
        /// <param name="team">Player's chosen team (A/B/C in this slice).</param>
        void PickTeam(TeamId team)
        {
            TitanOrbitSessionManager.Instance?.RequestTeam(team);
            gameObject.SetActive(false);
        }
    }
}
