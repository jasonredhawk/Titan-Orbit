using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side thruster type / color for the hull studio.
    /// Four JetFlame types (Ribbon, Default, Heavy, Soft). Color drives
    /// ParticleSystem Color over Lifetime. Follow-team locks the ramp to Color1;
    /// locked-chosen seeds the ramp from the picker, then each stop can be edited.
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
            public bool UseTeamColor => !IsCustom || FollowTeam != 0;
            public bool HasLifetimeStops => !UseTeamColor && LifeCustom != 0;

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
                LifeCustom = (byte)Mathf.Clamp(PlayerPrefs.GetInt(InstanceKey(PrefsKeyLifeCustom), 0), 0, 1),
                Life0Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife0), 0),
                Life1Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife1), 0),
                Life2Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife2), 0),
                Life3Packed = (uint)PlayerPrefs.GetInt(InstanceKey(PrefsKeyLife3), 0),
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
                LifeCustom = ghost.ThrusterLifeCustom,
                Life0Packed = ghost.ThrusterLife0Packed,
                Life1Packed = ghost.ThrusterLife1Packed,
                Life2Packed = ghost.ThrusterLife2Packed,
                Life3Packed = ghost.ThrusterLife3Packed,
            };
        }

        public static void CopyTo(ref ShipAccentColors accents, in Style style)
        {
            accents.ThrusterCustom = style.HasCustom;
            accents.ThrusterStyle = style.StyleIndex;
            accents.ThrusterFollowTeam = style.FollowTeam;
            accents.ThrusterColorPacked = style.ColorPacked;
            accents.ThrusterLifeCustom = style.LifeCustom;
            accents.ThrusterLife0Packed = style.Life0Packed;
            accents.ThrusterLife1Packed = style.Life1Packed;
            accents.ThrusterLife2Packed = style.Life2Packed;
            accents.ThrusterLife3Packed = style.Life3Packed;
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
        /// Studio Reset Jets: keep the current type, switch to Locked Chosen Color,
        /// and seed the well + lifetime ramp from default white.
        /// </summary>
        public static Style CreateLockedWhite(int styleIndex)
        {
            var style = new Style
            {
                HasCustom = 1,
                StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(styleIndex),
                FollowTeam = 0,
                ColorPacked = ShipAccentColors.Pack(new Color32(255, 255, 255, 255)),
                LifeCustom = 0,
            };
            WriteAnchorStops(ref style, TeamId.None);
            return style;
        }

        /// <summary>
        /// Authored flame name (Blue / Green / Purple / Red / Yellow / White).
        /// Follow-team uses Color1 hue. Locked Soft always starts on
        /// JetFlameSoftWhite so the lifetime palette can reach white.
        /// </summary>
        public static string ResolveFlameColorName(in Style style, TeamId team)
        {
            if (!style.UseTeamColor &&
                ResolveStyleIndex(style) == ThrusterVfxBank.SoftStyleIndex)
                return ThrusterVfxBank.NeutralFlameColorName;

            return ThrusterVfxBank.NearestFlameColorName(ResolveRawTint(style, team));
        }

        /// <summary>
        /// Well / gradient body color. Follow-team uses Color1; locked uses the
        /// picker. Value is lifted so a black pick still has a visible lifetime fade.
        /// </summary>
        public static Color32 ResolveTint(in Style style, TeamId team)
        {
            Color color = ResolveRawTint(style, team);
            Color.RGBToHSV(color, out float h, out float s, out float v);
            if (v < 0.22f)
                v = 0.22f;
            return ShipColorizeAccentApplier.Opaque(Color.HSVToRGB(h, s, v));
        }

        /// <summary>
        /// Team follow uses the authored team ramp. Locked uses edited stops when
        /// the player has touched a lifetime square; otherwise the Color well rebuilds
        /// the four-stop envelope.
        /// </summary>
        public static Gradient ResolveLifetime(in Style style, TeamId team)
        {
            if (style.UseTeamColor)
                return ThrusterFlameLifetimeColors.ForTeam(team);
            if (style.HasLifetimeStops)
            {
                return ThrusterFlameLifetimeColors.FromStops(
                    ShipAccentColors.Unpack(style.Life0Packed),
                    ShipAccentColors.Unpack(style.Life1Packed),
                    ShipAccentColors.Unpack(style.Life2Packed),
                    ShipAccentColors.Unpack(style.Life3Packed));
            }

            return ThrusterFlameLifetimeColors.FromAnchor(ResolveTint(style, team), team);
        }

        public static Color32 GetLifetimeStop(in Style style, int index, TeamId team)
        {
            index = Mathf.Clamp(index, 0, 3);
            if (style.HasLifetimeStops)
                return ShipAccentColors.Unpack(GetLifePacked(style, index));

            Gradient gradient = ResolveLifetime(style, team);
            GradientColorKey[] keys = gradient.colorKeys;
            if (keys != null && index < keys.Length)
                return ShipColorizeAccentApplier.Opaque(keys[index].color);

            return ShipColorizeAccentApplier.Opaque(gradient.Evaluate(
                ThrusterFlameLifetimeColors.SampleTimes[index]));
        }

        public static void WriteAnchorStops(ref Style style, TeamId team)
        {
            Gradient gradient = ThrusterFlameLifetimeColors.FromAnchor(ResolveTint(style, team), team);
            GradientColorKey[] keys = gradient.colorKeys;
            style.Life0Packed = PackKey(keys, 0);
            style.Life1Packed = PackKey(keys, 1);
            style.Life2Packed = PackKey(keys, 2);
            style.Life3Packed = PackKey(keys, 3);
            style.LifeCustom = 0;
        }

        public static void SetLifetimeStop(ref Style style, int index, Color32 color, TeamId team)
        {
            if (style.LifeCustom == 0)
                WriteAnchorStops(ref style, team);

            uint packed = ShipAccentColors.Pack(color);
            switch (index)
            {
                case 1:
                    style.Life1Packed = packed;
                    break;
                case 2:
                    style.Life2Packed = packed;
                    break;
                case 3:
                    style.Life3Packed = packed;
                    break;
                default:
                    style.Life0Packed = packed;
                    break;
            }

            style.LifeCustom = 1;
        }

        static uint GetLifePacked(in Style style, int index)
        {
            switch (index)
            {
                case 1: return style.Life1Packed;
                case 2: return style.Life2Packed;
                case 3: return style.Life3Packed;
                default: return style.Life0Packed;
            }
        }

        static uint PackKey(GradientColorKey[] keys, int index)
        {
            Color32 color = keys != null && index < keys.Length
                ? ShipColorizeAccentApplier.Opaque(keys[index].color)
                : new Color32(255, 255, 255, 255);
            return ShipAccentColors.Pack(color);
        }

        static Color32 ResolveRawTint(in Style style, TeamId team)
        {
            TeamId resolved = team == TeamId.None ? TeamId.TeamA : team;
            if (style.UseTeamColor)
                return ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(resolved));
            if (style.ColorPacked != 0)
                return ShipAccentColors.Unpack(style.ColorPacked);
            return ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(resolved));
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
