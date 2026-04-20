using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Single catalog row for Unity IAP (<see cref="ConfigurationBuilder.AddProduct"/>).
    /// </summary>
    [Serializable]
    public struct TitanOrbitIapCatalogEntry
    {
        [Tooltip("Must match Google Play Console / App Store Connect (and the remove-ads id below if applicable).")]
        public string productId;
        public ProductType productType;
    }

    /// <summary>
    /// Unity IAP bootstrap: configure catalog in the Inspector and mirror products in store consoles.
    /// Entitlements flow through <see cref="TitanOrbitEntitlements"/>.
    /// </summary>
    public class TitanOrbitIapManager : MonoBehaviour, IDetailedStoreListener
    {
        [SerializeField] bool initializeOnAwake = true;
        [Tooltip("Must match one catalog entry with type NonConsumable.")]
        [SerializeField] string removeAdsProductId = "remove_ads";
        [Tooltip("All IAP products; extend this list as you add SKUs.")]
        [SerializeField] TitanOrbitIapCatalogEntry[] catalog;

        IStoreController _controller;
        IExtensionProvider _extensions;

        void Reset()
        {
            removeAdsProductId = "remove_ads";
            catalog = new[]
            {
                new TitanOrbitIapCatalogEntry { productId = "remove_ads", productType = ProductType.NonConsumable }
            };
        }

        void Awake()
        {
            EnsureCatalogDefaults();
            TitanOrbitEntitlements.RegisterRemoveAdsProductId(removeAdsProductId);
            if (initializeOnAwake)
                InitializePurchasing();
        }

        void EnsureCatalogDefaults()
        {
            if (catalog == null || catalog.Length == 0)
            {
                catalog = new[]
                {
                    new TitanOrbitIapCatalogEntry { productId = "remove_ads", productType = ProductType.NonConsumable }
                };
            }

            if (string.IsNullOrWhiteSpace(removeAdsProductId))
                removeAdsProductId = "remove_ads";
        }

        /// <summary>Safe to call multiple times; second call is a no-op after success.</summary>
        public void InitializePurchasing()
        {
            if (!enabled)
                return;
            if (_controller != null)
                return;

            EnsureCatalogDefaults();

            var module = StandardPurchasingModule.Instance();
            var builder = ConfigurationBuilder.Instance(module);
            foreach (TitanOrbitIapCatalogEntry entry in catalog)
            {
                string id = entry.productId?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;
                builder.AddProduct(id, entry.productType);
            }

            UnityPurchasing.Initialize(this, builder);
        }

        public bool IsStoreReady => _controller != null;

        public void InitiatePurchase(string productId)
        {
            if (_controller == null)
            {
                Debug.LogWarning("[TitanOrbitIapManager] InitiatePurchase ignored (store not ready).");
                return;
            }

            if (string.IsNullOrWhiteSpace(productId))
                return;
            _controller.InitiatePurchase(productId.Trim());
        }

        public string GetLocalizedPriceString(string productId)
        {
            if (_controller == null || string.IsNullOrWhiteSpace(productId))
                return "";
            Product p = _controller.products.WithID(productId.Trim());
            return p?.metadata?.localizedPriceString ?? "";
        }

        /// <summary>On iOS triggers Apple restore; on other platforms re-reads non-consumable receipts.</summary>
        public void RestorePurchases(Action<bool, string> onFinished = null)
        {
            if (_controller == null || _extensions == null)
            {
                Debug.LogWarning("[TitanOrbitIapManager] RestorePurchases ignored (store not ready).");
                onFinished?.Invoke(false, "store_not_ready");
                return;
            }

#if (UNITY_IOS || UNITY_TVOS) && !UNITY_EDITOR
            var apple = _extensions.GetExtension<IAppleExtensions>();
            if (apple != null)
            {
                apple.RestoreTransactions((success, message) =>
                {
                    Debug.Log("[TitanOrbitIapManager] Apple restore result=" + success + " msg=" + message);
                    ReconcileNonConsumableEntitlements();
                    onFinished?.Invoke(success, message);
                });
                return;
            }
#endif
            ReconcileNonConsumableEntitlements();
            onFinished?.Invoke(true, null);
        }

        public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
        {
            _controller = controller;
            _extensions = extensions;
            Debug.Log("[TitanOrbitIapManager] Store initialized. PlayerId=" +
                      (UnityGameServicesBootstrap.PlayerId ?? "(not signed in / UGS not ready)"));
            ReconcileNonConsumableEntitlements();
        }

        public void OnInitializeFailed(InitializationFailureReason error)
        {
            Debug.LogWarning("[TitanOrbitIapManager] Init failed: " + error);
        }

        public void OnInitializeFailed(InitializationFailureReason error, string message)
        {
            Debug.LogWarning("[TitanOrbitIapManager] Init failed: " + error + " — " + message);
        }

        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs args)
        {
            string pid = args.purchasedProduct?.definition?.id ?? "";
            string tid = args.purchasedProduct?.transactionID ?? "";
            TitanOrbitEntitlements.NotifyPurchaseCompleted(pid, tid);
            return PurchaseProcessingResult.Complete;
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription)
        {
            Debug.LogWarning("[TitanOrbitIapManager] Purchase failed: " + failureDescription?.reason + " " +
                             failureDescription?.message);
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason)
        {
            Debug.LogWarning("[TitanOrbitIapManager] Purchase failed: " + failureReason);
        }

        void ReconcileNonConsumableEntitlements()
        {
            if (_controller == null || catalog == null)
                return;
            foreach (TitanOrbitIapCatalogEntry entry in catalog)
            {
                if (entry.productType != ProductType.NonConsumable)
                    continue;
                string id = entry.productId?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;
                Product p = _controller.products.WithID(id);
                if (p != null && p.hasReceipt)
                    TitanOrbitEntitlements.ApplyReconciledNonConsumable(id, true);
            }
        }

        /// <summary>Snapshot of configured catalog entries (for store UI).</summary>
        public IReadOnlyList<TitanOrbitIapCatalogEntry> GetCatalogSnapshot()
        {
            EnsureCatalogDefaults();
            return new List<TitanOrbitIapCatalogEntry>(catalog);
        }

        public bool TryGetStoreProduct(string productId, out Product product)
        {
            product = null;
            if (_controller == null || string.IsNullOrWhiteSpace(productId))
                return false;
            product = _controller.products.WithID(productId.Trim());
            return product != null;
        }

        public string GetProductLocalizedTitle(string productId)
        {
            if (!TryGetStoreProduct(productId, out var p))
                return productId ?? "";
            string t = p.metadata?.localizedTitle;
            return string.IsNullOrEmpty(t) ? (productId ?? "") : t;
        }

        /// <summary>Short status for store rows: Purchased / Available / Store not ready, etc.</summary>
        public string GetUiOwnershipLabel(string productId)
        {
            if (_controller == null)
                return "Store not ready";
            if (!TryGetStoreProduct(productId, out var p))
                return "Not in store";

            if (p.definition.type == ProductType.NonConsumable)
            {
                if (IsPurchasedOrHasReceipt(productId))
                    return "Purchased";
                return "Available";
            }

            if (p.definition.type == ProductType.Subscription)
                return p.hasReceipt ? "Active" : "Available";

            return "Available";
        }

        public bool IsPurchasedOrHasReceipt(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId))
                return false;
            string id = productId.Trim();
            if (!string.IsNullOrEmpty(TitanOrbitEntitlements.RemoveAdsProductId) &&
                string.Equals(id, TitanOrbitEntitlements.RemoveAdsProductId, StringComparison.Ordinal) &&
                TitanOrbitEntitlements.IsRemoveAdsOwned)
                return true;
            if (!TryGetStoreProduct(id, out var p))
                return false;
            if (p.definition.type == ProductType.NonConsumable || p.definition.type == ProductType.Subscription)
                return p.hasReceipt;
            return false;
        }

        public bool CanInitiatePurchase(string productId)
        {
            if (_controller == null || string.IsNullOrWhiteSpace(productId))
                return false;
            if (!TryGetStoreProduct(productId.Trim(), out var p))
                return false;
            if (IsPurchasedOrHasReceipt(productId) && p.definition.type == ProductType.NonConsumable)
                return false;
            return p.availableToPurchase;
        }
    }
}
