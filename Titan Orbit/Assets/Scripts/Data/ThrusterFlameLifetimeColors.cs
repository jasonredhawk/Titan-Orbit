using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Color-over-lifetime presets for thruster ParticleSystems.
    /// Team follow uses a per-team envelope (hot core → team Color1 → fade).
    /// Locked picks rebuild the same envelope from the picker color, including alpha.
    /// Presentation only — does not change sim or ghosts.
    /// </summary>
    public static class ThrusterFlameLifetimeColors
    {
        static readonly GradientColorKey[] ColorKeys = new GradientColorKey[4];
        static readonly GradientAlphaKey[] AlphaKeys = new GradientAlphaKey[4];
        static readonly Gradient Shared = new Gradient();

        public const int StopCount = 4;

        public static readonly float[] SampleTimes = { 0f, 0.22f, 0.55f, 1f };

        /// <summary>Team A–E Color1 plus a matching alpha envelope.</summary>
        public static Gradient ForTeam(TeamId team)
        {
            TeamId resolved = team == TeamId.None ? TeamId.TeamA : team;
            return FromAnchor(TeamColor1Palette.GetColor1(resolved), resolved);
        }

        /// <summary>
        /// Four distinct hues plus a fade: white-hot core, heat accent, body, dark tail.
        /// Nearby-hue ramps looked like a single tint on textured JetFlames.
        /// </summary>
        public static Gradient FromAnchor(Color body, TeamId teamHint = TeamId.None)
        {
            Color.RGBToHSV(body, out float hue, out float sat, out float val);
            Color hot = new Color(1f, 0.97f, 0.92f, 1f);
            Color heat;
            Color mid;
            Color tail;
            if (sat < 0.12f && val > 0.7f)
            {
                heat = new Color(1f, 0.92f, 0.72f, 1f);
                mid = new Color(0.96f, 0.96f, 0.96f, 1f);
                tail = new Color(0.38f, 0.38f, 0.40f, 1f);
            }
            else
            {
                sat = Mathf.Clamp(sat, 0.42f, 1f);
                val = Mathf.Clamp(val, 0.42f, 1f);
                float heatHue = Mathf.Repeat(hue + HeatHueOffset(teamHint, hue), 1f);
                heat = Color.HSVToRGB(heatHue, 0.55f, 1f);
                mid = Color.HSVToRGB(hue, sat, val);
                tail = Color.HSVToRGB(hue, Mathf.Min(1f, sat + 0.15f), val * 0.18f);
            }

            WriteTeamEnvelope(teamHint, out float a0, out float a2, out float tBright, out float tMid);

            ColorKeys[0] = new GradientColorKey(hot, 0f);
            ColorKeys[1] = new GradientColorKey(heat, tBright);
            ColorKeys[2] = new GradientColorKey(mid, tMid);
            ColorKeys[3] = new GradientColorKey(tail, 1f);

            AlphaKeys[0] = new GradientAlphaKey(a0, 0f);
            AlphaKeys[1] = new GradientAlphaKey(1f, tBright);
            AlphaKeys[2] = new GradientAlphaKey(a2, Mathf.Clamp01(tMid + 0.16f));
            AlphaKeys[3] = new GradientAlphaKey(0f, 1f);

            Shared.mode = GradientMode.Blend;
            Shared.SetKeys(ColorKeys, AlphaKeys);
            return Shared;
        }

        /// <summary>Player-authored RGB stops with the same alpha envelope as team presets.</summary>
        public static Gradient FromStops(Color stop0, Color stop1, Color stop2, Color stop3)
        {
            WriteTeamEnvelope(TeamId.None, out float a0, out float a2, out float tBright, out float tMid);
            ColorKeys[0] = new GradientColorKey(Opaque(stop0), 0f);
            ColorKeys[1] = new GradientColorKey(Opaque(stop1), tBright);
            ColorKeys[2] = new GradientColorKey(Opaque(stop2), tMid);
            ColorKeys[3] = new GradientColorKey(Opaque(stop3), 1f);
            AlphaKeys[0] = new GradientAlphaKey(a0, 0f);
            AlphaKeys[1] = new GradientAlphaKey(1f, tBright);
            AlphaKeys[2] = new GradientAlphaKey(a2, Mathf.Clamp01(tMid + 0.16f));
            AlphaKeys[3] = new GradientAlphaKey(0f, 1f);
            Shared.mode = GradientMode.Blend;
            Shared.SetKeys(ColorKeys, AlphaKeys);
            return Shared;
        }

        public static void SampleStops(Gradient gradient, Color[] dst)
        {
            if (dst == null)
                return;
            for (int i = 0; i < dst.Length && i < SampleTimes.Length; i++)
            {
                Color c = gradient.Evaluate(SampleTimes[i]);
                c.a = 1f;
                dst[i] = c;
            }
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }

        /// <summary>White→yellow heat for reds; white→cyan for cool teams.</summary>
        static float HeatHueOffset(TeamId team, float bodyHue)
        {
            switch (team)
            {
                case TeamId.TeamA: return 0.08f;
                case TeamId.TeamB: return -0.08f;
                case TeamId.TeamC: return 0.10f;
                case TeamId.TeamD: return 0.04f;
                case TeamId.TeamE: return -0.12f;
                default:
                    return bodyHue < 0.15f || bodyHue > 0.9f ? 0.08f : -0.06f;
            }
        }

        /// <summary>
        /// Per-team fade shape. Red holds the body longer; blue / purple fade sooner
        /// so cool flames do not read as a solid slab.
        /// </summary>
        static void WriteTeamEnvelope(
            TeamId team,
            out float spawnAlpha,
            out float bodyAlpha,
            out float brightTime,
            out float midTime)
        {
            switch (team)
            {
                case TeamId.TeamA:
                    spawnAlpha = 0.28f;
                    bodyAlpha = 0.78f;
                    brightTime = 0.16f;
                    midTime = 0.46f;
                    return;
                case TeamId.TeamB:
                    spawnAlpha = 0.16f;
                    bodyAlpha = 0.62f;
                    brightTime = 0.14f;
                    midTime = 0.42f;
                    return;
                case TeamId.TeamC:
                    spawnAlpha = 0.20f;
                    bodyAlpha = 0.70f;
                    brightTime = 0.15f;
                    midTime = 0.44f;
                    return;
                case TeamId.TeamD:
                    spawnAlpha = 0.24f;
                    bodyAlpha = 0.74f;
                    brightTime = 0.17f;
                    midTime = 0.48f;
                    return;
                case TeamId.TeamE:
                    spawnAlpha = 0.18f;
                    bodyAlpha = 0.64f;
                    brightTime = 0.14f;
                    midTime = 0.40f;
                    return;
                default:
                    spawnAlpha = 0.20f;
                    bodyAlpha = 0.70f;
                    brightTime = 0.16f;
                    midTime = 0.45f;
                    return;
            }
        }
    }
}
