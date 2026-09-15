using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side hull paint. Players pick a <see cref="ShipAccentPreset"/>;
    /// Color1 always comes from <see cref="TeamColor1Palette"/>. Packed Color2/3/Glow
    /// are copied from the preset so remotes see the same look without a new ghost field.
    /// Free players present factory preset 0 in a match. Customize Ship may hold an
    /// in-memory preview while <see cref="TitanOrbitCosmeticGate.IsHangarPreviewActive"/>.
    /// </summary>
    public static class LocalPlayerShipAccents
    {
        /// <summary>0 = Factory (baked Colorize). 1…N are authored presets.</summary>
        public const string PrefsKeyPreset = "TitanOrbit_ShipAccentPreset_v3";

        /// <summary>Kept so older builds do not reuse free-HSV Color2/3 as a custom flag.</summary>
        public const string PrefsKeyCustom = "TitanOrbit_ShipAccentCustom_v2";

        public const string PrefsKeyColor2 = "TitanOrbit_ShipAccentColor2_v2";
        public const string PrefsKeyColor3 = "TitanOrbit_ShipAccentColor3_v2";
        public const string PrefsKeyEmission = "TitanOrbit_ShipAccentEmission1_v2";
        public const string PrefsKeyEmission2 = "TitanOrbit_ShipAccentEmission2_v2";
        public const string PrefsKeyEmission3 = "TitanOrbit_ShipAccentEmission3_v2";

        static bool s_HasCache;
        static int s_PresetIndex;
        static ShipAccentColors s_Cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_HasCache = false;
            s_PresetIndex = 0;
            s_Cached = default;
        }

        /// <summary>
        /// Drops the in-memory paint cache so the next <see cref="Get"/> re-reads prefs
        /// (or factory defaults when Orbit Unlocked is not owned). Called when the
        /// entitlement flips mid-session.
        /// </summary>
        public static void InvalidateRuntimeCache()
        {
            s_HasCache = false;
        }

        /// <summary>
        /// Saved (or preview) preset index, or 0 (factory) when locked and not previewing.
        /// </summary>
        public static int GetPresetIndex()
        {
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked &&
                !(TitanOrbitCosmeticGate.IsHangarPreviewActive && s_HasCache))
                return 0;
            Get();
            return s_PresetIndex;
        }

        /// <summary>Factory or the selected preset's packed Color2 / Color3 / Glow.</summary>
        public static ShipAccentColors Get()
        {
            // --- Match clamp vs studio preview ---
            // [TITAN-ORBIT] Free players in a match always show factory paint.
            // Customize Ship sets IsHangarPreviewActive so Get() can return the
            // in-memory cache without writing PlayerPrefs.
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
            {
                if (TitanOrbitCosmeticGate.IsHangarPreviewActive && s_HasCache)
                    return s_Cached;
                return BuildPaint(0);
            }

            if (s_HasCache)
                return s_Cached;

            s_PresetIndex = TeamColor1Palette.WrapPresetIndex(
                PlayerPrefs.GetInt(InstanceKey(PrefsKeyPreset), 0));
            s_Cached = BuildPaint(s_PresetIndex);
            s_HasCache = true;
            return s_Cached;
        }

        public static ShipAccentColors ResolveForPresentation(bool isLocalOwner, in ShipAccentColors ghost)
        {
            if (!isLocalOwner)
                return ghost;

            ShipAccentColors paint = Get();
            LocalPlayerThrusterStyle.CopyTo(ref paint, LocalPlayerThrusterStyle.Get());
            return paint;
        }

        public static void SetPresetIndex(int index, bool flushToDisk = true)
        {
            int wrapped = TeamColor1Palette.WrapPresetIndex(index);
            if (!TitanOrbitCosmeticGate.AllowsCosmeticRead && wrapped != 0)
                return;

            s_PresetIndex = wrapped;
            s_Cached = BuildPaint(s_PresetIndex);
            s_HasCache = true;
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;

            PlayerPrefs.SetInt(InstanceKey(PrefsKeyPreset), s_PresetIndex);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyCustom), s_Cached.HasCustom);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor2), (int)s_Cached.Color2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor3), (int)s_Cached.Color3Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission), (int)s_Cached.EmissionPacked);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission2), (int)s_Cached.Emission2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission3), (int)s_Cached.Emission3Packed);
            if (flushToDisk)
                PlayerPrefs.Save();
        }

        /// <summary>Writes packed colors from a preset (or Factory). Prefer <see cref="SetPresetIndex"/>.</summary>
        public static void Set(ShipAccentColors accents, bool flushToDisk = true)
        {
            if (!accents.IsCustom)
            {
                SetPresetIndex(0, flushToDisk);
                return;
            }

            if (!TitanOrbitCosmeticGate.AllowsCosmeticRead)
                return;

            s_Cached = accents;
            s_HasCache = true;
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;

            PlayerPrefs.SetInt(InstanceKey(PrefsKeyCustom), 1);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor2), (int)accents.Color2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor3), (int)accents.Color3Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission), (int)accents.EmissionPacked);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission2), (int)accents.Emission2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission3), (int)accents.Emission3Packed);
            if (flushToDisk)
                PlayerPrefs.Save();
        }

        public static void Clear()
        {
            SetPresetIndex(0, flushToDisk: true);
        }

        /// <summary>
        /// Writes the in-memory preview to PlayerPrefs after Orbit Unlocked is granted
        /// while Customize Ship is still open. No-ops when the cache is empty.
        /// </summary>
        public static void PersistUnlockedFromCache()
        {
            if (!s_HasCache || !TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;
            SetPresetIndex(s_PresetIndex, flushToDisk: true);
        }

        static ShipAccentColors BuildPaint(int index)
        {
            if (!TeamColor1Palette.TryGetAccentPreset(index, out ShipAccentPreset preset) || preset == null)
                return ShipAccentColors.Default;

            return ShipAccentColors.FromCustom(
                ShipColorizeAccentApplier.Opaque(preset.color2),
                ShipColorizeAccentApplier.Opaque(preset.color3),
                ShipColorizeAccentApplier.Opaque(preset.emission1),
                ShipColorizeAccentApplier.Opaque(preset.emission2),
                ShipColorizeAccentApplier.Opaque(preset.emission3));
        }

        static string InstanceKey(string key) => TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(key);
    }
}
