using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side Colorize accent palette for the hull studio and match-join RPC.
    /// Persists on this device with PlayerPrefs (same pattern as
    /// <see cref="LocalPlayerDisplayName"/>). Color1 is never stored — it comes from
    /// the team material.
    /// <para>
    /// There is no Cloud Save yet, so a new machine starts at the material defaults
    /// until the player paints accents again.
    /// </para>
    /// Get() is cached. The visualizer may call it while syncing ship proxies —
    /// do not hit PlayerPrefs on that path every frame.
    /// </summary>
    public static class LocalPlayerShipAccents
    {
        /// <summary>PlayerPrefs key for the HasCustom flag (0 / 1).</summary>
        public const string PrefsKeyCustom = "TitanOrbit_ShipAccentCustom_v2";

        /// <summary>PlayerPrefs key for packed Color2.</summary>
        public const string PrefsKeyColor2 = "TitanOrbit_ShipAccentColor2_v2";

        /// <summary>PlayerPrefs key for packed Color3.</summary>
        public const string PrefsKeyColor3 = "TitanOrbit_ShipAccentColor3_v2";

        /// <summary>PlayerPrefs key for packed Emission1.</summary>
        public const string PrefsKeyEmission = "TitanOrbit_ShipAccentEmission1_v2";

        /// <summary>PlayerPrefs key for packed Emission2.</summary>
        public const string PrefsKeyEmission2 = "TitanOrbit_ShipAccentEmission2_v2";

        /// <summary>PlayerPrefs key for packed Emission3.</summary>
        public const string PrefsKeyEmission3 = "TitanOrbit_ShipAccentEmission3_v2";

        static bool s_HasCache;
        static ShipAccentColors s_Cached;

        /// <summary>[UNITY] Domain reload / Play Mode: drop the memory cache.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_HasCache = false;
            s_Cached = default;
        }

        /// <summary>Reads the saved palette, or <see cref="ShipAccentColors.Default"/> when unset.</summary>
        public static ShipAccentColors Get()
        {
            if (s_HasCache)
                return s_Cached;

            int custom = PlayerPrefs.GetInt(InstanceKey(PrefsKeyCustom), 0);
            if (custom == 0)
            {
                s_Cached = ShipAccentColors.Default;
                s_HasCache = true;
                return s_Cached;
            }

            s_Cached = new ShipAccentColors
            {
                HasCustom = 1,
                Color2Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyColor2), 0),
                Color3Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyColor3), 0),
                EmissionPacked = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyEmission), 0),
                Emission2Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyEmission2), 0),
                Emission3Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyEmission3), 0),
            };
            s_HasCache = true;
            return s_Cached;
        }

        /// <summary>
        /// Your hull uses the local prefs immediately. Remotes wait for the ghost.
        /// Same idea as the nameplate roster: the owner never waits on a round-trip.
        /// </summary>
        public static ShipAccentColors ResolveForPresentation(bool isLocalOwner, in ShipAccentColors ghost)
        {
            return isLocalOwner ? Get() : ghost;
        }

        /// <summary>
        /// Writes the palette for the next launch and for <see cref="ShipAccentColorsRpcClient"/>.
        /// <paramref name="flushToDisk"/> is false while dragging the picker so we do not
        /// hit disk every pixel.
        /// </summary>
        public static void Set(ShipAccentColors accents, bool flushToDisk = true)
        {
            s_Cached = accents;
            s_HasCache = true;
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyCustom), accents.HasCustom);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor2), (int)accents.Color2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor3), (int)accents.Color3Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission), (int)accents.EmissionPacked);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission2), (int)accents.Emission2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyEmission3), (int)accents.Emission3Packed);
            if (flushToDisk)
                PlayerPrefs.Save();
        }

        /// <summary>Clears the custom flag so ships use baked Colorize accents again.</summary>
        public static void Clear()
        {
            Set(ShipAccentColors.Default, flushToDisk: true);
        }

        static string InstanceKey(string key) => TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(key);
    }
}
