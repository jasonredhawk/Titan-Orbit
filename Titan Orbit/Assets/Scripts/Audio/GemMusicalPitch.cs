using UnityEngine;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Maps gem cargo / deposit amounts onto a C-major white-key piano ladder for SFX pitch.
    /// Shared by deposit metronome and gem collect (asteroid pickup) so both use the same clip language.
    /// <para>
    /// [TITAN-ORBIT] Designed for 55 white keys: value 1 = highest C, value 55 = lowest key.
    /// Within each octave cycle the note names are C, D, E, F, G, A, B; value 8 = C exactly one
    /// octave lower (pitch ÷ 2). Pitch uses true equal temperament from the value-1 root —
    /// <c>pitch = pitchForValue1 × 2^(−semitones/12)</c> — so shifting the root up moves every
    /// note by the same factor (intervals stay correct). The low bookend is only a floor clamp,
    /// not a stretch target (stretching min/max unevenly would squash the scale).
    /// </para>
    /// </summary>
    public static class GemMusicalPitch
    {
        /// <summary>
        /// [TITAN-ORBIT] Game piano width in white keys. Value 1..55 map to distinct keys;
        /// larger amounts clamp to the bottom key (world gems are sim-split to stay ≤ 55).
        /// </summary>
        public const int WhiteKeyCount = 55;

        /// <summary>Notes per C-major octave cycle (C D E F G A B).</summary>
        public const int NotesPerOctave = 7;

        /// <summary>
        /// Semitone offsets from C for each scale degree (equal temperament).
        /// Index 0 = C, 1 = D, 2 = E, 3 = F, 4 = G, 5 = A, 6 = B.
        /// Used as semitones <b>down</b> from the top C so higher gem value → lower pitch.
        /// </summary>
        static readonly int[] DegreeSemitonesFromC = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>
        /// Converts a gem amount into an <see cref="AudioSource.pitch"/> multiplier on the white-key ladder.
        /// </summary>
        /// <param name="gemAmount">
        /// Gem value for this SFX (deposit chunk or cargo delta). Rounded to an integer key;
        /// sub-0.5 amounts that still play map to value 1 (highest C).
        /// </param>
        /// <param name="pitchForValue1">
        /// Pitch at value 1 (highest C / root). Every other key is tuned from this with equal temperament.
        /// Unity AudioClip pitch clamps at 3 — keep this ≤ 3.
        /// </param>
        /// <param name="pitchForValue55">
        /// Lowest allowed pitch (floor). Does not compress the scale — notes that would go lower
        /// play at this floor. Raise/lower it by the <b>same factor</b> as <paramref name="pitchForValue1"/>
        /// when shifting the whole piano up or down.
        /// </param>
        /// <returns>Pitch multiplier for <see cref="AudioSource.pitch"/> (always &gt; 0).</returns>
        public static float ResolvePitch(float gemAmount, float pitchForValue1, float pitchForValue55)
        {
            // --- Normalize designer bookends ---
            // [STANDARD] Guard against zero/negative inspector values (Unity pitch must be > 0).
            float rootPitch = Mathf.Max(0.0001f, pitchForValue1);
            float pitchFloor = Mathf.Max(0.0001f, pitchForValue55);
            // If someone swaps min/max in the Inspector, keep value 1 as the high root.
            if (pitchFloor > rootPitch)
            {
                float swap = pitchFloor;
                pitchFloor = rootPitch;
                rootPitch = swap;
            }

            // --- Amount → white-key index (0 = highest C, 54 = lowest key) ---
            int value = Mathf.RoundToInt(gemAmount);
            if (value < 1)
                value = 1;
            if (value > WhiteKeyCount)
                value = WhiteKeyCount;

            int keyIndex = value - 1;

            // --- True equal temperament from the root C ---
            // [TITAN-ORBIT] value 1 → root; value 2 (D) → root × 2^(-2/12); value 8 (C) → root / 2.
            // Same multiply on root + floor = equal shift of the whole piano; intervals stay exact.
            float semitonesDown = SemitonesDownFromTopC(keyIndex);
            float pitch = rootPitch * Mathf.Pow(2f, -semitonesDown / 12f);

            // Floor only — do not re-stretch into [floor, root] (that was the uneven squash).
            if (pitch < pitchFloor)
                pitch = pitchFloor;

            return pitch;
        }

        /// <summary>
        /// Semitones <b>down</b> from the highest C for a white-key index (0..54).
        /// Value 1 → 0; value 2 (D) → 2; value 8 (C) → 12 (one octave lower).
        /// </summary>
        /// <param name="keyIndex">Zero-based white-key index (value − 1).</param>
        /// <returns>Non-negative semitone distance below the top C.</returns>
        public static float SemitonesDownFromTopC(int keyIndex)
        {
            // --- Degree + octave ---
            // [TITAN-ORBIT] C D E F G A B, then wrap; each full cycle adds one octave of drop.
            int safeIndex = Mathf.Max(0, keyIndex);
            int degree = safeIndex % NotesPerOctave;
            int octaveDown = safeIndex / NotesPerOctave;
            int degreeSemitone = DegreeSemitonesFromC[degree];
            return degreeSemitone + (12 * octaveDown);
        }
    }
}
