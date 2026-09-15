using System;
using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Local map from store receipts to what this client is allowed to do.
    /// Titan Orbit sells one non-consumable: <see cref="OrbitUnlockedProductIdDefault"/>
    /// ("Orbit Unlocked"). Owning it turns ads off, unlocks hangar cosmetics, and
    /// auto-grants the +1 loadout slot in a match.
    /// <para>
    /// Persistence is PlayerPrefs on this device (plus Unity IAP receipts on restore).
    /// There is no Cloud Save / receipt-validation backend yet — extend
    /// <see cref="PurchaseRecordedForPlayer"/> when that lands.
    /// </para>
    /// Client-only. Dedicated server never reads these flags; the +1 slot still goes
    /// through the existing <c>ClaimRewardedBonusSlotCommand</c> RPC.
    /// </summary>
    public static class TitanOrbitEntitlements
    {
        /// <summary>
        /// Store product id to create in Google Play Console / App Store Connect.
        /// Must match <see cref="TitanOrbitIapManager"/> catalog.
        /// </summary>
        public const string OrbitUnlockedProductIdDefault = "orbit_unlocked";

        /// <summary>
        /// Older test SKU. Receipts still grant Orbit Unlocked so Editor / device
        /// restores from the remove-ads era keep working.
        /// </summary>
        public const string LegacyRemoveAdsProductId = "remove_ads";

        /// <summary>Player-facing IAP name used by menus when the store title is empty.</summary>
        public const string OrbitUnlockedDisplayName = "Orbit Unlocked";

        /// <summary>PlayerPrefs key for the master entitlement (0/1).</summary>
        const string OrbitUnlockedPlayerPrefsKey = "TitanOrbit_OrbitUnlockedOwned_v1";

        /// <summary>
        /// [LEGACY] Previous remove-ads key. Read on first launch so testers who
        /// already bought <c>remove_ads</c> are treated as Orbit Unlocked owners.
        /// </summary>
        const string LegacyRemoveAdsPlayerPrefsKey = "TitanOrbit_RemoveAdsOwned_v1";

        /// <summary>
        /// Fired after a store purchase is recorded, with product id and transaction id.
        /// Hook for a future cloud write. [STANDARD] C# event — subscribers must unsubscribe.
        /// </summary>
        public static event Action<string, string> PurchaseRecordedForPlayer;

        /// <summary>
        /// Fired when <see cref="IsOrbitUnlockedOwned"/> flips (load, restore, purchase,
        /// or Editor grant/revoke). Customize UI and cosmetic caches listen here.
        /// </summary>
        public static event Action OrbitUnlockedOwnershipChanged;

        /// <summary>
        /// [LEGACY] Same event as <see cref="OrbitUnlockedOwnershipChanged"/>.
        /// Ads code that already subscribed under the old name keeps compiling.
        /// </summary>
        public static event Action RemoveAdsOwnershipChanged
        {
            add => OrbitUnlockedOwnershipChanged += value;
            remove => OrbitUnlockedOwnershipChanged -= value;
        }

        /// <summary>Last UGS player id seen when recording a purchase (debug / UI).</summary>
        public static string LastPurchasePlayerId { get; private set; }

        /// <summary>
        /// Product id that grants Orbit Unlocked. Set from <see cref="TitanOrbitIapManager"/>
        /// at Awake so Inspector overrides win over the default constant.
        /// </summary>
        public static string OrbitUnlockedProductId { get; private set; } = OrbitUnlockedProductIdDefault;

        /// <summary>
        /// [LEGACY] Same as <see cref="OrbitUnlockedProductId"/> for older IAP helper calls.
        /// </summary>
        public static string RemoveAdsProductId => OrbitUnlockedProductId;

        /// <summary>
        /// True when this device owns Orbit Unlocked (purchase, restore, or migrated
        /// remove-ads prefs). Source of truth for ads, cosmetics, and auto +1 slot.
        /// </summary>
        public static bool IsOrbitUnlockedOwned { get; private set; }

        /// <summary>
        /// [LEGACY] Ads gate alias. Orbit Unlocked is what actually turns videos off.
        /// </summary>
        public static bool IsRemoveAdsOwned => IsOrbitUnlockedOwned;

        /// <summary>
        /// [UNITY] Static ctor runs before first property access. We migrate the old
        /// remove-ads PlayerPrefs bit so a returning tester is not shown ads again.
        /// </summary>
        static TitanOrbitEntitlements()
        {
            IsOrbitUnlockedOwned = ReadOwnedFromPrefs();
        }

        /// <summary>
        /// Called by <see cref="TitanOrbitIapManager"/> before UnityPurchasing.Initialize.
        /// Empty / whitespace falls back to <see cref="OrbitUnlockedProductIdDefault"/>.
        /// </summary>
        /// <param name="productId">Inspector product id, or null to keep the default.</param>
        public static void RegisterOrbitUnlockedProductId(string productId)
        {
            // --- Register catalog id ---
            // [TITAN-ORBIT] One SKU. The manager also keeps the legacy remove_ads row
            // in the catalog so old receipts can still reconcile.
            OrbitUnlockedProductId = string.IsNullOrWhiteSpace(productId)
                ? OrbitUnlockedProductIdDefault
                : productId.Trim();
        }

        /// <summary>
        /// [LEGACY] Older manager called this for remove-ads. Forwards to
        /// <see cref="RegisterOrbitUnlockedProductId"/>.
        /// </summary>
        public static void RegisterRemoveAdsProductId(string productId)
        {
            RegisterOrbitUnlockedProductId(productId);
        }

        /// <summary>
        /// Call from <see cref="TitanOrbitIapManager.ProcessPurchase"/> after the store
        /// confirms a transaction. Grants Orbit Unlocked for the live or legacy SKU.
        /// </summary>
        /// <param name="productId">Unity IAP product definition id.</param>
        /// <param name="transactionId">Store transaction id (may be empty in Editor).</param>
        public static void NotifyPurchaseCompleted(string productId, string transactionId)
        {
            // --- Record purchase ---
            string playerId = UnityGameServicesBootstrap.PlayerId;
            LastPurchasePlayerId = playerId;
            PurchaseRecordedForPlayer?.Invoke(productId ?? "", transactionId ?? "");
            Debug.Log($"[TitanOrbitEntitlements] Purchase {productId} for player {playerId ?? "(none)"}");

            if (ProductGrantsOrbitUnlocked(productId))
                SetOrbitUnlockedOwned(true);
        }

        /// <summary>
        /// Used after store init / Apple restore when a non-consumable already has a receipt.
        /// Does nothing when <paramref name="hasReceipt"/> is false.
        /// </summary>
        public static void ApplyReconciledNonConsumable(string productId, bool hasReceipt)
        {
            // --- Restore from receipt ---
            if (!hasReceipt || string.IsNullOrEmpty(productId))
                return;
            if (ProductGrantsOrbitUnlocked(productId))
                SetOrbitUnlockedOwned(true);
        }

        /// <summary>
        /// True when this product id is Orbit Unlocked or the legacy remove-ads SKU.
        /// </summary>
        public static bool ProductGrantsOrbitUnlocked(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId))
                return false;
            string id = productId.Trim();

            // Live SKU (Inspector override or default).
            if (!string.IsNullOrEmpty(OrbitUnlockedProductId) &&
                string.Equals(id, OrbitUnlockedProductId, StringComparison.Ordinal))
                return true;

            if (string.Equals(id, OrbitUnlockedProductIdDefault, StringComparison.Ordinal))
                return true;

            // [LEGACY] remove_ads receipts from the first IAP pass.
            return string.Equals(id, LegacyRemoveAdsProductId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Writes the master flag and both PlayerPrefs keys (new + legacy) so older
        /// readers and the new code stay in sync. No-ops when the value is unchanged.
        /// </summary>
        /// <param name="owned">True after purchase / restore / Editor grant.</param>
        public static void SetOrbitUnlockedOwned(bool owned)
        {
            // --- Persist entitlement ---
            if (IsOrbitUnlockedOwned == owned)
                return;

            IsOrbitUnlockedOwned = owned;
            int bit = owned ? 1 : 0;
            // [TITAN-ORBIT] Services cannot reference NetCode MPPM key helpers. Same
            // raw keys as the old remove-ads flag — one purchase per Editor process.
            PlayerPrefs.SetInt(OrbitUnlockedPlayerPrefsKey, bit);
            PlayerPrefs.SetInt(LegacyRemoveAdsPlayerPrefsKey, bit);
            PlayerPrefs.Save();
            OrbitUnlockedOwnershipChanged?.Invoke();
        }

        /// <summary>
        /// [LEGACY] Ads-era setter. Forwards to <see cref="SetOrbitUnlockedOwned"/>.
        /// </summary>
        public static void SetRemoveAdsOwned(bool owned)
        {
            SetOrbitUnlockedOwned(owned);
        }

        /// <summary>
        /// Reads the new key first, then migrates the legacy remove-ads bit if needed.
        /// </summary>
        static bool ReadOwnedFromPrefs()
        {
            // --- Load + migrate ---
            if (PlayerPrefs.HasKey(OrbitUnlockedPlayerPrefsKey))
                return PlayerPrefs.GetInt(OrbitUnlockedPlayerPrefsKey, 0) != 0;

            bool legacyOwned = PlayerPrefs.GetInt(LegacyRemoveAdsPlayerPrefsKey, 0) != 0;
            if (legacyOwned)
            {
                // One-time copy so later launches hit the new key only.
                PlayerPrefs.SetInt(OrbitUnlockedPlayerPrefsKey, 1);
                PlayerPrefs.Save();
            }

            return legacyOwned;
        }
    }
}
