using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Color-over-lifetime presets for thruster ParticleSystems.
    /// Team follow uses a per-team hue ramp (hot core → team Color1 → dark tail).
    /// Locked picks rebuild the same hues from the picker. Alpha is hold-then-fade.
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

            WriteColorStopTimes(teamHint, out float tBright, out float tMid);

            ColorKeys[0] = new GradientColorKey(hot, 0f);
            ColorKeys[1] = new GradientColorKey(heat, tBright);
            ColorKeys[2] = new GradientColorKey(mid, tMid);
            ColorKeys[3] = new GradientColorKey(tail, 1f);

            // Archanor jets live ~0.1s. A fade-in/fade-out envelope strobes ~10 Hz.
            // Hold alpha at 1, then drop only at the tail — same shape as the prefab.
            WriteHoldThenFadeAlpha();

            Shared.mode = GradientMode.Blend;
            Shared.SetKeys(ColorKeys, AlphaKeys);
            return Shared;
        }

        /// <summary>Player-authored RGB stops with the same alpha envelope as team presets.</summary>
        public static Gradient FromStops(Color stop0, Color stop1, Color stop2, Color stop3)
        {
            // Last color sits at the hold-alpha time (0.71). A key at t=1 is
            // already faded out, so the 4th well could not be seen.
            ColorKeys[0] = new GradientColorKey(Opaque(stop0), SampleTimes[0]);
            ColorKeys[1] = new GradientColorKey(Opaque(stop1), SampleTimes[1]);
            ColorKeys[2] = new GradientColorKey(Opaque(stop2), SampleTimes[2]);
            ColorKeys[3] = new GradientColorKey(Opaque(stop3), 0.71f);
            WriteHoldThenFadeAlpha();
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
        /// Color-stop times only. Cool teams shift the body earlier; alpha stays
        /// a hold-then-fade so short-lived JetFlame particles do not strobe.
        /// </summary>
        static void WriteColorStopTimes(TeamId team, out float brightTime, out float midTime)
        {
            switch (team)
            {
                case TeamId.TeamA:
                    brightTime = 0.16f;
                    midTime = 0.46f;
                    return;
                case TeamId.TeamB:
                    brightTime = 0.14f;
                    midTime = 0.42f;
                    return;
                case TeamId.TeamC:
                    brightTime = 0.15f;
                    midTime = 0.44f;
                    return;
                case TeamId.TeamD:
                    brightTime = 0.17f;
                    midTime = 0.48f;
                    return;
                case TeamId.TeamE:
                    brightTime = 0.14f;
                    midTime = 0.40f;
                    return;
                default:
                    brightTime = 0.16f;
                    midTime = 0.45f;
                    return;
            }
        }

        /// <summary>
        /// Authored ModularJetFlame2 holds alpha until ~0.71 then fades. Reuse that
        /// so team / picker hues do not introduce a spawn-dim → vanish cycle.
        /// </summary>
        static void WriteHoldThenFadeAlpha()
        {
            AlphaKeys[0] = new GradientAlphaKey(1f, 0f);
            AlphaKeys[1] = new GradientAlphaKey(1f, 0.22f);
            AlphaKeys[2] = new GradientAlphaKey(1f, 0.71f);
            AlphaKeys[3] = new GradientAlphaKey(0f, 1f);
        }
    }
}
