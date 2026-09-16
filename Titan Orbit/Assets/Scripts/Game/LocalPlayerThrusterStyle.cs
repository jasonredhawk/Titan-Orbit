using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side thruster type for the hull studio.
    /// Four JetFlame types (Ribbon, Default, Heavy, Soft). Color is always
    /// the match team's Color1 — players only pick the type.
    /// Free players present the default jet in a match. Customize Ship may hold an
    /// in-memory preview while <see cref="TitanOrbitCosmeticGate.IsHangarPreviewActive"/>.
    /// </summary>
    public static class LocalPlayerThrusterStyle
    {
        public const string PrefsKeyCustom = "TitanOrbit_ThrusterCustom_v2";
        public const string PrefsKeyStyle = "TitanOrbit_ThrusterStyle_v2";
        public const string PrefsKeyColor = "TitanOrbit_ThrusterColorPacked_v2";
        public const string PrefsKeyFollow = "TitanOrbit_ThrusterFollowTeam_v2";
        public const string PrefsKeyLifeCustom = "TitanOrbit_ThrusterLifeCustom_v3";
        public const string PrefsKeyLife0 = "TitanOrbit_ThrusterLife0_v3";
        public const string PrefsKeyLife1 = "TitanOrbit_ThrusterLife1_v3";
        public const string PrefsKeyLife2 = "TitanOrbit_ThrusterLife2_v3";
        public const string PrefsKeyLife3 = "TitanOrbit_ThrusterLife3_v3";

        public struct Style
        {
            public byte HasCustom;
            public byte StyleIndex;
            public byte FollowTeam;
            public uint ColorPacked;
            public byte LifeCustom;
            public uint Life0Packed;
            public uint Life1Packed;
            public uint Life2Packed;
            public uint Life3Packed;

            public bool IsCustom => HasCustom != 0;

            /// <summary>Jets always follow team Color1. Packed color / lifetime fields stay on the ghost for layout.</summary>
            public bool UseTeamColor => true;

            public int CacheKey
            {
                get
                {
                    unchecked
                    {
                        int hash = HasCustom;
                        hash = (hash * 397) ^ StyleIndex;
                        hash = (hash * 397) ^ FollowTeam;
                        hash = (hash * 397) ^ (int)ColorPacked;
                        hash = (hash * 397) ^ LifeCustom;
                        hash = (hash * 397) ^ (int)Life0Packed;
                        hash = (hash * 397) ^ (int)Life1Packed;
                        hash = (hash * 397) ^ (int)Life2Packed;
                        hash = (hash * 397) ^ (int)Life3Packed;
                        return hash;
                    }
                }
            }
        }

        static bool s_HasCache;
        static Style s_Cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_HasCache = false;
            s_Cached = default;
        }

        /// <summary>
        /// Drops the in-memory jet cache so the next <see cref="Get"/> re-reads prefs
        /// (or the default style when Orbit Unlocked is not owned).
        /// </summary>
        public static void InvalidateRuntimeCache()
        {
            s_HasCache = false;
        }

        public static Style Get()
        {
            // --- Match clamp vs studio preview ---
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
            {
                if (TitanOrbitCosmeticGate.IsHangarPreviewActive && s_HasCache)
                    return NormalizeTeamColor(s_Cached);
                return default;
            }

            if (s_HasCache)
                return NormalizeTeamColor(s_Cached);

            int custom = PlayerPrefs.GetInt(InstanceKey(PrefsKeyCustom), 0);
            if (custom == 0)
            {
                s_Cached = default;
                s_HasCache = true;
                return s_Cached;
            }

            s_Cached = NormalizeTeamColor(new Style
            {
                HasCustom = 1,
                StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(
                    PlayerPrefs.GetInt(InstanceKey(PrefsKeyStyle), ThrusterVfxBank.DefaultStyleIndex)),
                FollowTeam = (byte)Mathf.Clamp(PlayerPrefs.GetInt(InstanceKey(PrefsKeyFollow), 1), 0, 1),
                ColorPacked = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyColor), 0),
                LifeCustom = (byte)Mathf.Clamp(PlayerPrefs.GetInt(InstanceKey(PrefsKeyLifeCustom), 0), 0, 1),
                Life0Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife0), 0),
                Life1Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife1), 0),
                Life2Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife2), 0),
                Life3Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife3), 0),
            });
            s_HasCache = true;
            return s_Cached;
        }

        public static Style ResolveForPresentation(bool isLocalOwner, in ShipAccentColors ghost)
        {
            if (isLocalOwner)
                return Get();
            return FromGhost(ghost);
        }

        public static Style FromGhost(in ShipAccentColors ghost)
        {
            return NormalizeTeamColor(new Style
            {
                HasCustom = ghost.ThrusterCustom,
                StyleIndex = ghost.ThrusterStyle,
                FollowTeam = ghost.ThrusterFollowTeam,
                ColorPacked = ghost.ThrusterColorPacked,
                LifeCustom = ghost.ThrusterLifeCustom,
                Life0Packed = ghost.ThrusterLife0Packed,
                Life1Packed = ghost.ThrusterLife1Packed,
                Life2Packed = ghost.ThrusterLife2Packed,
                Life3Packed = ghost.ThrusterLife3Packed,
            });
        }

        public static void CopyTo(ref ShipAccentColors accents, in Style style)
        {
            Style normalized = NormalizeTeamColor(style);
            accents.ThrusterCustom = normalized.HasCustom;
            accents.ThrusterStyle = normalized.StyleIndex;
            accents.ThrusterFollowTeam = normalized.FollowTeam;
            accents.ThrusterColorPacked = normalized.ColorPacked;
            accents.ThrusterLifeCustom = normalized.LifeCustom;
            accents.ThrusterLife0Packed = normalized.Life0Packed;
            accents.ThrusterLife1Packed = normalized.Life1Packed;
            accents.ThrusterLife2Packed = normalized.Life2Packed;
            accents.ThrusterLife3Packed = normalized.Life3Packed;
        }

        public static void Set(Style style)
        {
            if (!TitanOrbitCosmeticGate.AllowsCosmeticRead && style.HasCustom != 0)
                return;

            style.StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(style.StyleIndex);
            style = NormalizeTeamColor(style);
            s_Cached = style;
            s_HasCache = true;
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;

            PlayerPrefs.SetInt(InstanceKey(PrefsKeyCustom), style.HasCustom);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyStyle), style.StyleIndex);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyFollow), style.FollowTeam);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor), (int)style.ColorPacked);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyLifeCustom), style.LifeCustom);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyLife0), (int)style.Life0Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyLife1), (int)style.Life1Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyLife2), (int)style.Life2Packed);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyLife3), (int)style.Life3Packed);
            PlayerPrefs.Save();
        }

        public static void Clear()
        {
            Set(default);
        }

        /// <summary>
        /// Writes the in-memory preview to PlayerPrefs after Orbit Unlocked is granted
        /// while Customize Ship is still open.
        /// </summary>
        public static void PersistUnlockedFromCache()
        {
            if (!s_HasCache || !TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;
            Set(s_Cached);
        }

        /// <summary>Studio Reset Jets: Default type, locked to team Color1.</summary>
        public static Style CreateTeamFollow(int styleIndex)
        {
            return NormalizeTeamColor(new Style
            {
                HasCustom = 1,
                StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(styleIndex),
            });
        }

        /// <summary>
        /// Authored flame name (Blue / Green / Purple / Red / Yellow).
        /// Always the nearest authored variant to team Color1.
        /// </summary>
        public static string ResolveFlameColorName(in Style style, TeamId team)
        {
            return ThrusterVfxBank.NearestFlameColorName(ResolveRawTint(team));
        }

        /// <summary>Well / gradient body color — always team Color1.</summary>
        public static Color32 ResolveTint(in Style style, TeamId team)
        {
            return ResolveRawTint(team);
        }

        /// <summary>Team Color1 ramp: hot core, team body, dark tail.</summary>
        public static Gradient ResolveLifetime(in Style style, TeamId team)
        {
            return ThrusterFlameLifetimeColors.ForTeam(team);
        }

        public static int ResolveStyleIndex(in Style style)
        {
            if (!style.IsCustom)
                return ThrusterVfxBank.DefaultStyleIndex;
            return ThrusterVfxBank.WrapStyleIndex(style.StyleIndex);
        }

        /// <summary>
        /// Drops leftover locked-color / lifetime-stop prefs so ghosts stay team-follow.
        /// Packed color fields remain on the wire for ghost layout compatibility.
        /// </summary>
        static Style NormalizeTeamColor(Style style)
        {
            if (style.HasCustom == 0)
                return default;

            style.FollowTeam = 1;
            style.ColorPacked = 0;
            style.LifeCustom = 0;
            style.Life0Packed = 0;
            style.Life1Packed = 0;
            style.Life2Packed = 0;
            style.Life3Packed = 0;
            return style;
        }

        static Color32 ResolveRawTint(TeamId team)
        {
            TeamId resolved = team == TeamId.None ? TeamId.TeamA : team;
            return ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(resolved));
        }

        static string InstanceKey(string key) => TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(key);
    }
}
