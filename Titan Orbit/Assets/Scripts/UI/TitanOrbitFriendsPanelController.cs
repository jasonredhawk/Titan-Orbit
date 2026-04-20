using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TitanOrbit.Services;
using Unity.Services.Friends;
using Unity.Services.Friends.Models;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Binds UGS Friends to UI. Attach to the Friends panel (alongside Shift <c>FriendsPanelManager</c> for animations).
    /// </summary>
    public class TitanOrbitFriendsPanelController : MonoBehaviour
    {
        [SerializeField] TMP_InputField addFriendByIdOrNameField;
        [SerializeField] Button addFriendButton;
        [SerializeField] Button refreshButton;
        [SerializeField] TextMeshProUGUI friendsListOutput;
        [SerializeField] TextMeshProUGUI statusOutput;

        void OnEnable()
        {
            if (addFriendButton != null)
            {
                addFriendButton.onClick.RemoveListener(OnAddFriendClicked);
                addFriendButton.onClick.AddListener(OnAddFriendClicked);
            }
            if (refreshButton != null)
            {
                refreshButton.onClick.RemoveListener(OnRefreshClicked);
                refreshButton.onClick.AddListener(OnRefreshClicked);
            }
            _ = RefreshFriendsUiAsync();
        }

        void OnDisable()
        {
            if (addFriendButton != null)
                addFriendButton.onClick.RemoveListener(OnAddFriendClicked);
            if (refreshButton != null)
                refreshButton.onClick.RemoveListener(OnRefreshClicked);
        }

        async void OnAddFriendClicked()
        {
            string raw = addFriendByIdOrNameField != null ? addFriendByIdOrNameField.text : null;
            raw = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                SetStatus("Enter a friend name or player id.");
                return;
            }

            if (!await TitanOrbitFriendsCoordinator.EnsureFriendsInitializedAsync())
            {
                SetStatus("Friends unavailable (sign in required).");
                return;
            }

            try
            {
                if (raw.IndexOf('-') >= 0 || raw.Length > 28)
                    await FriendsService.Instance.AddFriendAsync(raw);
                else
                    await FriendsService.Instance.AddFriendByNameAsync(raw);
                SetStatus("Request sent.");
                await RefreshFriendsUiAsync();
            }
            catch (System.Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        async void OnRefreshClicked()
        {
            await RefreshFriendsUiAsync();
        }

        async Task RefreshFriendsUiAsync()
        {
            if (!UnityGameServicesBootstrap.IsSignedIn)
            {
                SetStatus("Sign in to use Friends.");
                SetListText("(not signed in)");
                return;
            }

            if (!await TitanOrbitFriendsCoordinator.EnsureFriendsInitializedAsync())
            {
                SetStatus("Could not initialize Friends.");
                SetListText("");
                return;
            }

            try
            {
                await FriendsService.Instance.ForceRelationshipsRefreshAsync();
                var sb = new StringBuilder();
                IReadOnlyList<Relationship> friends = FriendsService.Instance.Friends;
                if (friends == null || friends.Count == 0)
                    sb.AppendLine("No friends yet.");
                else
                {
                    foreach (var r in friends)
                    {
                        string id = r.Member != null ? r.Member.Id : r.Id;
                        sb.AppendLine(id ?? "?");
                    }
                }
                SetListText(sb.ToString());
                SetStatus("Friends: " + friends.Count);
            }
            catch (System.Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        void SetStatus(string msg)
        {
            if (statusOutput != null)
                statusOutput.text = msg;
        }

        void SetListText(string t)
        {
            if (friendsListOutput != null)
                friendsListOutput.text = t;
        }
    }
}
