using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Single catalog row for Unity IAP (<see cref="ConfigurationBuilder.AddProduct"/>).
    /// Product ids must match Google Play Console / App Store Connect exactly.
    /// </summary>
    [Serializable]
    public struct TitanOrbitIapCatalogEntry
    {
        [Tooltip("Store product id. Orbit Unlocked is orbit_unlocked.")]
        public string productId;
        public ProductType productType;
    }

    /// <summary>
    /// Unity IAP bootstrap on the DontDestroyOnLoad services host.
    /// Catalog is one player-facing SKU — Orbit Unlocked — plus the legacy
    /// <c>remove_ads</c> row so old receipts still restore.
    /// <para>
    /// Entitlements flow through <see cref="TitanOrbitEntitlements"/>. No shop UI lives
    /// here; <c>OrbitUnlockedPurchaseScreen</c> calls <see cref="InitiatePurchase"/>.
    /// </para>
    /// Client-only. Dedicated server never creates this component
    /// (<see cref="TitanOrbitServicesRuntimeBootstrap"/>).
    /// </summary>
    public class TitanOrbitIapManager : MonoBehaviour, IDetailedStoreListener
    {
        [SerializeField] bool initializeOnAwake = true;

        [Tooltip("Live non-consumable SKU. Mirror this id in the store consoles.")]
        [SerializeField] string orbitUnlockedProductId = TitanOrbitEntitlements.OrbitUnlockedProductIdDefault;

        [Tooltip("All IAP products. Keep orbit_unlocked plus legacy remove_ads for restore.")]
        [SerializeField] TitanOrbitIapCatalogEntry[] catalog;

        IStoreController _controller;
        IExtensionProvider _extensions;

        /// <summary>
        /// [UNITY] Reset runs in the Editor when the component is first added.
        /// Runtime AddComponent skips Reset — <see cref="EnsureCatalogDefaults"/> covers that.
        /// </summary>
        void Reset()
        {
            // --- Editor defaults ---
            orbitUnlockedProductId = TitanOrbitEntitlements.OrbitUnlockedProductIdDefault;
            catalog = BuildDefaultCatalog();
        }

        /// <summary>
        /// Registers the product id on entitlements before UnityPurchasing starts.
        /// </summary>
        void Awake()
        {
            EnsureCatalogDefaults();
            TitanOrbitEntitlements.RegisterOrbitUnlockedProductId(orbitUnlockedProductId);
        }

        /// <summary>
        /// [UNITY] Start is async so we can wait for a UGS guest session. IAP can
        /// initialize without auth, but the player id in logs is nicer with it.
        /// </summary>
        async void Start()
        {
            // --- Unity lifecycle ---
            if (!initializeOnAwake || _controller != null)
                return;
            try
            {
                await UnityGameServicesBootstrap.EnsureGuestSessionForOnlineAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TitanOrbitIapManager] UGS guest session before IAP: " + e.Message);
            }

            InitializePurchasing();
        }

        /// <summary>
        /// Fills an empty catalog and guarantees both the live SKU and the legacy
        /// remove-ads id are present so restore can see either receipt.
        /// </summary>
        void EnsureCatalogDefaults()
        {
            // --- Ensure setup ---
            if (string.IsNullOrWhiteSpace(orbitUnlockedProductId))
                orbitUnlockedProductId = TitanOrbitEntitlements.OrbitUnlockedProductIdDefault;

            if (catalog == null || catalog.Length == 0)
            {
                catalog = BuildDefaultCatalog();
                return;
            }

            bool hasLive = false;
            bool hasLegacy = false;
            for (int i = 0; i < catalog.Length; i++)
            {
                string id = catalog[i].productId?.Trim();
                if (string.Equals(id, orbitUnlockedProductId, StringComparison.Ordinal) ||
                    string.Equals(id, TitanOrbitEntitlements.OrbitUnlockedProductIdDefault, StringComparison.Ordinal))
                    hasLive = true;
                if (string.Equals(id, TitanOrbitEntitlements.LegacyRemoveAdsProductId, StringComparison.Ordinal))
                    hasLegacy = true;
            }

            if (hasLive && hasLegacy)
                return;

            // Scene instances from the remove-ads era only listed one row — append the missing ids.
            var expanded = new List<TitanOrbitIapCatalogEntry>(catalog);
            if (!hasLive)
            {
                expanded.Add(new TitanOrbitIapCatalogEntry
                {
                    productId = orbitUnlockedProductId,
                    productType = ProductType.NonConsumable
                });
            }

            if (!hasLegacy)
            {
                expanded.Add(new TitanOrbitIapCatalogEntry
                {
                    productId = TitanOrbitEntitlements.LegacyRemoveAdsProductId,
                    productType = ProductType.NonConsumable
                });
            }

            catalog = expanded.ToArray();
        }

        /// <summary>Live Orbit Unlocked row plus legacy remove-ads for restore.</summary>
        static TitanOrbitIapCatalogEntry[] BuildDefaultCatalog()
        {
            return new[]
            {
                new TitanOrbitIapCatalogEntry
                {
                    productId = TitanOrbitEntitlements.OrbitUnlockedProductIdDefault,
                    productType = ProductType.NonConsumable
                },
                new TitanOrbitIapCatalogEntry
                {
                    productId = TitanOrbitEntitlements.LegacyRemoveAdsProductId,
                    productType = ProductType.NonConsumable
                }
            };
        }

        /// <summary>Safe to call multiple times; second call is a no-op after success.</summary>
        public void InitializePurchasing()
        {
            // --- InitializePurchasing ---
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

        /// <summary>True after <see cref="OnInitialized"/> — buy/restore need this.</summary>
        public bool IsStoreReady => _controller != null;

        /// <summary>Inspector / runtime product id for the live SKU.</summary>
        public string OrbitUnlockedProductId
        {
            get
            {
                EnsureCatalogDefaults();
                return orbitUnlockedProductId;
            }
        }

        /// <summary>Starts a store purchase. No-ops when the controller is not ready.</summary>
        public void InitiatePurchase(string productId)
        {
            // --- InitiatePurchase ---
            if (_controller == null)
            {
                Debug.LogWarning("[TitanOrbitIapManager] InitiatePurchase ignored (store not ready).");
                return;
            }

            if (string.IsNullOrWhiteSpace(productId))
                return;
            _controller.InitiatePurchase(productId.Trim());
        }

        /// <summary>Convenience: buy the live Orbit Unlocked SKU.</summary>
        public void InitiateOrbitUnlockedPurchase()
        {
            InitiatePurchase(OrbitUnlockedProductId);
        }

        /// <summary>Localized price from the store, or empty when the catalog is not ready.</summary>
        public string GetLocalizedPriceString(string productId)
        {
            // --- Compute value ---
            if (_controller == null || string.IsNullOrWhiteSpace(productId))
                return "";
            Product p = _controller.products.WithID(productId.Trim());
            return p?.metadata?.localizedPriceString ?? "";
        }

        /// <summary>On iOS triggers Apple restore; on other platforms re-reads non-consumable receipts.</summary>
        public void RestorePurchases(Action<bool, string> onFinished = null)
        {
            // --- RestorePurchases ---
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

        /// <summary>[UNITY] Store finished initializing. We immediately restore owned non-consumables.</summary>
        public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
        {
            // --- OnInitialized ---
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

        /// <summary>
        /// Store confirmed a purchase. We grant entitlements immediately and mark
        /// the transaction complete (no pending server validation in v1).
        /// </summary>
        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs args)
        {
            // --- ProcessPurchase ---
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

        /// <summary>
        /// Walks the catalog and grants Orbit Unlocked for any non-consumable with a receipt.
        /// </summary>
        void ReconcileNonConsumableEntitlements()
        {
            // --- ReconcileNonConsumableEntitlements ---
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
            // --- Attempt resolution ---
            product = null;
            if (_controller == null || string.IsNullOrWhiteSpace(productId))
                return false;
            product = _controller.products.WithID(productId.Trim());
            return product != null;
        }

        public string GetProductLocalizedTitle(string productId)
        {
            // --- Compute value ---
            if (!TryGetStoreProduct(productId, out var p))
                return productId ?? "";
            string t = p.metadata?.localizedTitle;
            return string.IsNullOrEmpty(t) ? (productId ?? "") : t;
        }

        /// <summary>Short status for store rows: Purchased / Available / Store not ready, etc.</summary>
        public string GetUiOwnershipLabel(string productId)
        {
            // --- Compute value ---
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

        /// <summary>
        /// True when local entitlements already own this SKU, or the store receipt exists.
        /// </summary>
        public bool IsPurchasedOrHasReceipt(string productId)
        {
            // --- IsPurchasedOrHasReceipt ---
            if (string.IsNullOrWhiteSpace(productId))
                return false;
            string id = productId.Trim();

            // Local flag wins so Editor grants and migrated remove-ads show as owned
            // even when the fake Editor store has no receipt.
            if (TitanOrbitEntitlements.ProductGrantsOrbitUnlocked(id) &&
                TitanOrbitEntitlements.IsOrbitUnlockedOwned)
                return true;

            if (!TryGetStoreProduct(id, out var p))
                return false;
            if (p.definition.type == ProductType.NonConsumable || p.definition.type == ProductType.Subscription)
                return p.hasReceipt;
            return false;
        }

        public bool CanInitiatePurchase(string productId)
        {
            // --- CanInitiatePurchase ---
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
