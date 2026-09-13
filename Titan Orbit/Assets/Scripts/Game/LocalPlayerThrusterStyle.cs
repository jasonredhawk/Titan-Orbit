using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side thruster type / tint for the hull studio.
    /// Four JetFlame types (Classic, Modular, Heavy, Soft). Follow-team tints with
    /// match Color1; locked color is an HSV pick stored as packed RGBA.
    /// </summary>
    public static class LocalPlayerThrusterStyle
    {
        public const string PrefsKeyCustom = "TitanOrbit_ThrusterCustom_v2";
        public const string PrefsKeyStyle = "TitanOrbit_ThrusterStyle_v2";
        public const string PrefsKeyColor = "TitanOrbit_ThrusterColorPacked_v2";
        public const string PrefsKeyFollow = "TitanOrbit_ThrusterFollowTeam_v2";

        public struct Style
        {
            public byte HasCustom;
            public byte StyleIndex;
            public byte FollowTeam;
            public uint ColorPacked;

            public bool IsCustom => HasCustom != 0;
            public bool UseTeamColor => !IsCustom || FollowTeam != 0;

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

        public static Style Get()
        {
            if (s_HasCache)
                return s_Cached;

            int custom = PlayerPrefs.GetInt(InstanceKey(PrefsKeyCustom), 0);
            if (custom == 0)
            {
                s_Cached = default;
                s_HasCache = true;
                return s_Cached;
            }

            s_Cached = new Style
            {
                HasCustom = 1,
                StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(
                    PlayerPrefs.GetInt(InstanceKey(PrefsKeyStyle), ThrusterVfxBank.DefaultStyleIndex)),
                FollowTeam = (byte)Mathf.Clamp(PlayerPrefs.GetInt(InstanceKey(PrefsKeyFollow), 1), 0, 1),
                ColorPacked = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyColor), 0),
            };
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
            return new Style
            {
                HasCustom = ghost.ThrusterCustom,
                StyleIndex = ghost.ThrusterStyle,
                FollowTeam = ghost.ThrusterFollowTeam,
                ColorPacked = ghost.ThrusterColorPacked,
            };
        }

        public static void CopyTo(ref ShipAccentColors accents, in Style style)
        {
            accents.ThrusterCustom = style.HasCustom;
            accents.ThrusterStyle = style.StyleIndex;
            accents.ThrusterFollowTeam = style.FollowTeam;
            accents.ThrusterColorPacked = style.ColorPacked;
        }

        public static void Set(Style style)
        {
            style.StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(style.StyleIndex);
            s_Cached = style;
            s_HasCache = true;
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyCustom), style.HasCustom);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyStyle), style.StyleIndex);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyFollow), style.FollowTeam);
            PlayerPrefs.SetInt(InstanceKey(PrefsKeyColor), (int)style.ColorPacked);
            PlayerPrefs.Save();
        }

        public static void Clear()
        {
            Set(default);
        }

        /// <summary>
        /// Flame tint. Follow-team uses the match Color1 palette; locked uses the picker.
        /// </summary>
        public static Color32 ResolveTint(in Style style, TeamId team)
        {
            if (style.UseTeamColor || style.ColorPacked == 0)
            {
                TeamId resolved = team == TeamId.None ? TeamId.TeamA : team;
                return ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(resolved));
            }

            return ShipAccentColors.Unpack(style.ColorPacked);
        }

        public static int ResolveStyleIndex(in Style style)
        {
            if (!style.IsCustom)
                return ThrusterVfxBank.DefaultStyleIndex;
            return ThrusterVfxBank.WrapStyleIndex(style.StyleIndex);
        }

        static string InstanceKey(string key) => TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(key);
    }
}
