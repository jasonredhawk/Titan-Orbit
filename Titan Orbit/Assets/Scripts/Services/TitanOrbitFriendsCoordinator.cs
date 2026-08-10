using System;
using System.Threading.Tasks;
using Unity.Services.Friends;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Ensures UGS Friends is initialized after Authentication. Call <see cref="EnsureFriendsInitializedAsync"/> before Friends APIs.
    /// </summary>
    public static class TitanOrbitFriendsCoordinator
    {
        static bool _friendsInitialized;

        public static bool IsFriendsApiReady => _friendsInitialized;

        public static async Task<bool> EnsureFriendsInitializedAsync()
        {
            // --- Ensure setup ---
            if (_friendsInitialized)
                return true;
            if (!UnityGameServicesBootstrap.IsSignedIn)
            {
                Debug.LogWarning("[TitanOrbitFriendsCoordinator] Not signed in; cannot initialize Friends.");
                return false;
            }

            try
            {
                await FriendsService.Instance.InitializeAsync();
                _friendsInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitFriendsCoordinator] Initialize failed: " + ex.Message);
                return false;
            }
        }

        public static void ResetAfterAuthChange()
        {
            _friendsInitialized = false;
        }
    }
}
