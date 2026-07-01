using TitanOrbit.Core;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>Handles team join clicks on the JoinButton itself (not label children).</summary>
    [RequireComponent(typeof(Button))]
    public class TeamJoinButton : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] TeamId team;

        Button _button;

        public void Configure(TeamId teamId)
        {
            team = teamId;
        }

        void Awake()
        {
            _button = GetComponent<Button>();
            DisableChildRaycasts();
        }

        void OnEnable()
        {
            DisableChildRaycasts();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_button != null && !_button.interactable)
                return;

            Debug.Log($"[TeamJoinButton] Join clicked for {team}.");
            if (TitanOrbitSessionManager.Instance == null)
            {
                Debug.LogError("[TeamJoinButton] Session manager missing.");
                return;
            }

            TitanOrbitSessionManager.Instance.RequestTeam(team);
        }

        void DisableChildRaycasts()
        {
            foreach (var graphic in GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.gameObject != gameObject)
                    graphic.raycastTarget = false;
            }
        }
    }
}
