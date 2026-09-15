using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-owned Colorize <c>_Color1</c> for each playable team, plus
    /// authored accent presets (Color2 / Color3 / Glow). Players pick a preset;
    /// they never freely recolor those slots, so a red team cannot look blue.
    /// <para>
    /// Edit <c>Assets/Resources/TeamColor1Palette.asset</c> in the Inspector.
    /// Use <b>Create Accent Preset</b> to add more Color2/3/glow assets.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "TeamColor1Palette",
        menuName = "Titan Orbit/Team Color 1 Palette",
        order = 65)]
    public class TeamColor1Palette : ScriptableObject
    {
        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "TeamColor1Palette";

        [Header("Colorize Color1 (players cannot change)")]
        [Tooltip("Team A Color1 — usually red.")]
        public Color teamA = new Color(0.9f, 0.25f, 0.25f, 1f);

        [Tooltip("Team B Color1 — usually blue.")]
        public Color teamB = new Color(0.25f, 0.4f, 0.9f, 1f);

        [Tooltip("Team C Color1 — usually green.")]
        public Color teamC = new Color(0.2f, 0.7f, 0.28f, 1f);

        [Tooltip("Team D Color1 — usually orange.")]
        public Color teamD = new Color(0.95f, 0.55f, 0.12f, 1f);

        [Tooltip("Team E Color1 — usually purple.")]
        public Color teamE = new Color(0.65f, 0.25f, 0.85f, 1f);

        [Header("Accent presets (Color2 / Color3 / Glow)")]
        [Tooltip("Index 0 on Customize Ship is Default (baked Colorize). These are 1…N.")]
        public List<ShipAccentPreset> accentPresets = new List<ShipAccentPreset>();

        static TeamColor1Palette s_Cached;

        /// <summary>[UNITY] Domain reload: drop the Resources cache.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Cached = null;

        /// <summary>Loads the Resources palette once per domain, or null if the asset is missing.</summary>
        public static TeamColor1Palette LoadDefault()
        {
            if (s_Cached != null)
                return s_Cached;
            s_Cached = Resources.Load<TeamColor1Palette>(ResourcesLoadName);
            return s_Cached;
        }

        /// <summary>
        /// Colorize Color1 for <paramref name="team"/>. Falls back to
        /// <see cref="TeamIdExtensions.ToColor"/> when the asset is missing
        /// so hulls still tint in a fresh checkout.
        /// </summary>
        public static Color GetColor1(TeamId team)
        {
            TeamColor1Palette palette = LoadDefault();
            if (palette != null)
                return palette.GetColor1Instance(team);
            return team.ToColor();
        }

        /// <summary>Default plus every non-null authored preset.</summary>
        public int CycleCount
        {
            get
            {
                int n = 1;
                if (accentPresets == null)
                    return n;
                for (int i = 0; i < accentPresets.Count; i++)
                {
                    if (accentPresets[i] != null)
                        n++;
                }

                return n;
            }
        }

        /// <summary>Wraps 0 = Default, then authored presets in list order.</summary>
        public static int WrapPresetIndex(int index)
        {
            TeamColor1Palette palette = LoadDefault();
            int n = palette != null ? palette.CycleCount : 1;
            if (n < 1)
                n = 1;
            int i = index % n;
            return i < 0 ? i + n : i;
        }

        public static string GetPresetDisplayName(int index)
        {
            int wrapped = WrapPresetIndex(index);
            if (wrapped <= 0)
                return "Default";
            if (TryGetAccentPreset(wrapped, out ShipAccentPreset preset) && preset != null)
                return string.IsNullOrWhiteSpace(preset.displayName) ? preset.name : preset.displayName;
            return "Default";
        }

        /// <summary>
        /// Authored preset for cycle index <paramref name="index"/>.
        /// Index 0 is Default (baked Colorize) and returns false.
        /// </summary>
        public static bool TryGetAccentPreset(int index, out ShipAccentPreset preset)
        {
            preset = null;
            int wrapped = WrapPresetIndex(index);
            if (wrapped <= 0)
                return false;

            TeamColor1Palette palette = LoadDefault();
            if (palette == null || palette.accentPresets == null)
                return false;

            int remaining = wrapped;
            for (int i = 0; i < palette.accentPresets.Count; i++)
            {
                ShipAccentPreset row = palette.accentPresets[i];
                if (row == null)
                    continue;
                remaining--;
                if (remaining == 0)
                {
                    preset = row;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Instance lookup used by the Inspector-authored fields.</summary>
        public Color GetColor1Instance(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return Opaque(teamA);
                case TeamId.TeamB: return Opaque(teamB);
                case TeamId.TeamC: return Opaque(teamC);
                case TeamId.TeamD: return Opaque(teamD);
                case TeamId.TeamE: return Opaque(teamE);
                default: return Color.white;
            }
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }
    }
}
