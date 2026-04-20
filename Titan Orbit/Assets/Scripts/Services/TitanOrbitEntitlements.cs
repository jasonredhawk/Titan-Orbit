using System;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Hooks for mapping store purchases to the current UGS player id (<see cref="UnityGameServicesBootstrap.PlayerId"/>).
    /// Extend with Cloud Save, Economy, or a backend when you validate receipts server-side.
    /// </summary>
    public static class TitanOrbitEntitlements
    {
        const string RemoveAdsPlayerPrefsKey = "TitanOrbit_RemoveAdsOwned_v1";

        public static event Action<string, string> PurchaseRecordedForPlayer;

        /// <summary>Fired when <see cref="IsRemoveAdsOwned"/> changes after load, reconciliation, or purchase.</summary>
        public static event Action RemoveAdsOwnershipChanged;

        /// <summary>Last UGS player id used when recording a purchase (for debugging and local entitlement UI).</summary>
        public static string LastPurchasePlayerId { get; private set; }

        /// <summary>Product id that grants remove-ads; set from <see cref="TitanOrbitIapManager"/> at runtime.</summary>
        public static string RemoveAdsProductId { get; private set; }

        /// <summary>Local entitlement: remove-ads IAP owned or restored (also persisted in PlayerPrefs).</summary>
        public static bool IsRemoveAdsOwned { get; private set; }

        static TitanOrbitEntitlements()
        {
            IsRemoveAdsOwned = PlayerPrefs.GetInt(RemoveAdsPlayerPrefsKey, 0) != 0;
        }

        /// <summary>Called by <see cref="TitanOrbitIapManager"/> before building the product catalog.</summary>
        public static void RegisterRemoveAdsProductId(string productId)
        {
            RemoveAdsProductId = string.IsNullOrWhiteSpace(productId) ? "" : productId.Trim();
        }

        /// <summary>Call from <see cref="TitanOrbitIapManager"/> after a successful <c>ProcessPurchase</c>.</summary>
        public static void NotifyPurchaseCompleted(string productId, string transactionId)
        {
            string playerId = UnityGameServicesBootstrap.PlayerId;
            LastPurchasePlayerId = playerId;
            PurchaseRecordedForPlayer?.Invoke(productId ?? "", transactionId ?? "");
            Debug.Log($"[TitanOrbitEntitlements] Purchase {productId} for player {playerId ?? "(none)"}");

            if (!string.IsNullOrEmpty(RemoveAdsProductId) &&
                string.Equals(productId, RemoveAdsProductId, StringComparison.Ordinal))
                SetRemoveAdsOwned(true);
        }

        /// <summary>Used after store init / restore when a non-consumable already has a receipt.</summary>
        public static void ApplyReconciledNonConsumable(string productId, bool hasReceipt)
        {
            if (!hasReceipt || string.IsNullOrEmpty(productId))
                return;
            if (!string.IsNullOrEmpty(RemoveAdsProductId) &&
                string.Equals(productId, RemoveAdsProductId, StringComparison.Ordinal))
                SetRemoveAdsOwned(true);
        }

        public static void SetRemoveAdsOwned(bool owned)
        {
            if (IsRemoveAdsOwned == owned)
                return;
            IsRemoveAdsOwned = owned;
            PlayerPrefs.SetInt(RemoveAdsPlayerPrefsKey, owned ? 1 : 0);
            PlayerPrefs.Save();
            RemoveAdsOwnershipChanged?.Invoke();
        }
    }
}
