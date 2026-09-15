using TitanOrbit.Services;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Hangar paywall for ship cosmetics. Orbit Unlocked owners persist hull / badge /
    /// jet picks to PlayerPrefs and take them into a match.
    /// <para>
    /// Free players may still <b>preview</b> every look inside Customize Ship. That
    /// session is <see cref="IsHangarPreviewActive"/> — stores update an in-memory
    /// cache only. Closing the studio (or ending the preview) drops the cache so
    /// <see cref="LocalPlayerShipAccents.Get"/> (and badge / thruster) return factory
    /// defaults again. Prefs are never wiped; a later purchase restores any older save.
    /// </para>
    /// Client presentation only. Remotes see whatever the local owner publishes via
    /// <see cref="ShipAccentColorsRpcClient"/> / name RPCs — preview must not send those.
    /// </summary>
    public static class TitanOrbitCosmeticGate
    {
        /// <summary>
        /// True when hangar cosmetics may be written to PlayerPrefs and published
        /// in a match. Same flag that turns ads off.
        /// </summary>
        public static bool IsCustomizationUnlocked => TitanOrbitEntitlements.IsOrbitUnlockedOwned;

        /// <summary>
        /// True while Customize Ship is open for a free player (or any player).
        /// Stores may return the in-memory preview cache so the studio hull updates.
        /// </summary>
        public static bool IsHangarPreviewActive { get; private set; }

        /// <summary>
        /// True when Get() may return a non-default look: owned prefs, or an
        /// in-studio preview cache.
        /// </summary>
        public static bool AllowsCosmeticRead =>
            IsCustomizationUnlocked || IsHangarPreviewActive;

        /// <summary>
        /// [UNITY] Domain reload / Play Mode: subscribe so a mid-session purchase
        /// (or Editor grant) commits the preview or drops caches.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void HookOwnership()
        {
            IsHangarPreviewActive = false;
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged -= OnOwnershipChanged;
            TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged += OnOwnershipChanged;
        }

        /// <summary>
        /// Starts a Customize Ship session. Free players begin on factory defaults
        /// so leftover preview cache from a previous visit cannot leak.
        /// </summary>
        public static void BeginHangarPreview()
        {
            // --- Open studio ---
            IsHangarPreviewActive = true;
            if (IsCustomizationUnlocked)
                return;

            LocalPlayerShipAccents.InvalidateRuntimeCache();
            LocalPlayerThrusterStyle.InvalidateRuntimeCache();
            LocalPlayerBadge.ClearPreview();
        }

        /// <summary>
        /// Ends the studio session. Free players drop the preview cache so a match
        /// (and the next menu paint) shows factory / none / default jets again.
        /// </summary>
        public static void EndHangarPreview()
        {
            // --- Close studio ---
            if (!IsHangarPreviewActive)
                return;

            IsHangarPreviewActive = false;
            if (IsCustomizationUnlocked)
                return;

            LocalPlayerShipAccents.InvalidateRuntimeCache();
            LocalPlayerThrusterStyle.InvalidateRuntimeCache();
            LocalPlayerBadge.ClearPreview();
        }

        /// <summary>
        /// Purchase / revoke mid-session. If they bought while previewing, commit
        /// the look they are staring at. Otherwise drop caches.
        /// </summary>
        static void OnOwnershipChanged()
        {
            // --- Entitlement flip ---
            if (IsCustomizationUnlocked && IsHangarPreviewActive)
            {
                LocalPlayerShipAccents.PersistUnlockedFromCache();
                LocalPlayerThrusterStyle.PersistUnlockedFromCache();
                LocalPlayerBadge.PersistUnlockedFromPreview();
            }
            else if (!IsCustomizationUnlocked)
            {
                LocalPlayerShipAccents.InvalidateRuntimeCache();
                LocalPlayerThrusterStyle.InvalidateRuntimeCache();
                LocalPlayerBadge.ClearPreview();
            }

            ShipAccentColorsRpcClient.NotifyChanged();
        }
    }
}
