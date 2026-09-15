using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side preferred profile badge for the Main Menu picker.
    /// Persists across launches with PlayerPrefs so the player does not re-pick every session.
    /// <para>
    /// In a match, nameplates read <see cref="PlayerNameRosterCache"/> (filled by
    /// <see cref="SetPlayerNameCommand"/> / <see cref="PlayerNameAnnounceRpc"/>).
    /// This store is the local source of truth before / between matches, and the payload
    /// <see cref="PlayerNameRpcClient"/> sends after GoInGame.
    /// </para>
    /// Id 0 means no badge until the player picks one.
    /// Free players read as none in a match. Customize Ship may hold an in-memory
    /// preview while <see cref="TitanOrbitCosmeticGate.IsHangarPreviewActive"/>.
    /// </summary>
    public static class LocalPlayerBadge
    {
        /// <summary>PlayerPrefs key for the last badge chosen on the Main Menu.</summary>
        public const string PrefsKey = "TitanOrbit_PlayerBadgeId_v1";

        static bool s_HasPreview;
        static int s_PreviewId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_HasPreview = false;
            s_PreviewId = PlayerBadgeIdUtil.None;
        }

        /// <summary>
        /// Reads the saved badge id, or <see cref="PlayerBadgeIdUtil.None"/> when missing / invalid.
        /// Free players return the studio preview while Customize Ship is open, otherwise none.
        /// </summary>
        public static int Get()
        {
            if (TitanOrbitCosmeticGate.IsCustomizationUnlocked)
            {
                int raw = PlayerPrefs.GetInt(InstancePrefsKey, PlayerBadgeIdUtil.None);
                return PlayerBadgeIdUtil.Sanitize(raw);
            }

            if (TitanOrbitCosmeticGate.IsHangarPreviewActive && s_HasPreview)
                return PlayerBadgeIdUtil.Sanitize(s_PreviewId);

            return PlayerBadgeIdUtil.None;
        }

        /// <summary>
        /// Saves a sanitized badge id for owners. Free players only keep it as a
        /// studio preview — prefs are not written.
        /// </summary>
        /// <param name="badgeId">Filename-stable id from Badge (N).png, or 0 for none.</param>
        public static void Set(int badgeId)
        {
            int cleaned = PlayerBadgeIdUtil.Sanitize(badgeId);
            if (TitanOrbitCosmeticGate.IsCustomizationUnlocked)
            {
                PlayerPrefs.SetInt(InstancePrefsKey, cleaned);
                PlayerPrefs.Save();
                return;
            }

            if (!TitanOrbitCosmeticGate.IsHangarPreviewActive && cleaned != PlayerBadgeIdUtil.None)
                return;

            s_PreviewId = cleaned;
            s_HasPreview = true;
        }

        /// <summary>Drops the studio-only badge so match reads return none again.</summary>
        public static void ClearPreview()
        {
            s_HasPreview = false;
            s_PreviewId = PlayerBadgeIdUtil.None;
        }

        /// <summary>
        /// Writes the studio preview to PlayerPrefs after Orbit Unlocked is granted
        /// while Customize Ship is still open.
        /// </summary>
        public static void PersistUnlockedFromPreview()
        {
            if (!s_HasPreview || !TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;
            PlayerPrefs.SetInt(InstancePrefsKey, PlayerBadgeIdUtil.Sanitize(s_PreviewId));
            PlayerPrefs.Save();
        }

        static string InstancePrefsKey => TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(PrefsKey);
    }
}
