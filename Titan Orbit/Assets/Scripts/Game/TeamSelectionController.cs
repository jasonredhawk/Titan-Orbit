using TitanOrbit.Core;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    public class TeamSelectionController : MonoBehaviour
    {
        [SerializeField] Button teamAButton;
        [SerializeField] Button teamBButton;
        [SerializeField] Button teamCButton;

        void Start()
        {
            if (teamAButton != null) teamAButton.onClick.AddListener(() => PickTeam(TeamId.TeamA));
            if (teamBButton != null) teamBButton.onClick.AddListener(() => PickTeam(TeamId.TeamB));
            if (teamCButton != null) teamCButton.onClick.AddListener(() => PickTeam(TeamId.TeamC));
        }

        void PickTeam(TeamId team)
        {
            TitanOrbitSessionManager.Instance?.RequestTeam(team);
            gameObject.SetActive(false);
        }
    }
}
