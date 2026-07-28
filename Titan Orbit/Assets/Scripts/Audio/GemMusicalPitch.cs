using UnityEngine;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Maps gem cargo / deposit amounts onto a full chromatic piano for SFX pitch
    /// (all 88 keys: white and black / semitones). Shared by deposit metronome and gem
    /// collect (asteroid pickup) so both use the same clip language.
    /// <para>
    /// [TITAN-ORBIT] Designed for a standard 88-key piano span: value 1 = highest C (C8),
    /// value 88 = lowest A (A0). Each gem-value step is exactly one semitone — including
    /// sharps/flats — so the ladder has finer resolution than the old white-keys-only map.
    /// Pitch uses true equal temperament from the value-1 root —
    /// <c>pitch = pitchForValue1 × 2^(−(value−1)/12)</c> — so shifting the root up moves every
    /// note by the same factor (intervals stay correct). The low bookend is only a floor clamp,
    /// not a stretch target (stretching min/max unevenly would squash the scale).
    /// </para>
    /// </summary>
    public static class GemMusicalPitch
    {
        /// <summary>
        /// [TITAN-ORBIT] Full piano width in chromatic keys (A0…C8). Value 1..88 map to
        /// distinct pitches; larger amounts clamp to the bottom key (world gems are sim-split
        /// to stay ≤ 88).
        /// </summary>
        public const int PianoKeyCount = 88;

        /// <summary>
        /// Legacy alias for <see cref="PianoKeyCount"/>. Prefer <see cref="PianoKeyCount"/>.
        /// </summary>
        public const int WhiteKeyCount = PianoKeyCount;

        /// <summary>Semitones in one octave (equal temperament). Value N+12 = one octave below N.</summary>
        public const int NotesPerOctave = 12;

        /// <summary>
        /// Converts a gem amount into an <see cref="AudioSource.pitch"/> multiplier on the
        /// chromatic piano ladder.
        /// </summary>
        /// <param name="gemAmount">
        /// Gem value for this SFX (deposit chunk or cargo delta). Rounded to an integer key;
        /// sub-0.5 amounts that still play map to value 1 (highest C).
        /// </param>
        /// <param name="pitchForValue1">
        /// Pitch at value 1 (highest C / root). Every other key is tuned from this with equal temperament.
        /// Unity AudioClip pitch clamps at 3 — keep this ≤ 3.
        /// </param>
        /// <param name="pitchForLowestKey">
        /// Lowest allowed pitch (floor). Does not compress the scale — notes that would go lower
        /// play at this floor. Raise/lower it by the <b>same factor</b> as <paramref name="pitchForValue1"/>
        /// when shifting the whole piano up or down.
        /// </param>
        /// <returns>Pitch multiplier for <see cref="AudioSource.pitch"/> (always &gt; 0).</returns>
        public static float ResolvePitch(float gemAmount, float pitchForValue1, float pitchForLowestKey)
        {
            // --- Normalize designer bookends ---
            // [STANDARD] Guard against zero/negative inspector values (Unity pitch must be > 0).
            float rootPitch = Mathf.Max(0.0001f, pitchForValue1);
            float pitchFloor = Mathf.Max(0.0001f, pitchForLowestKey);
            // If someone swaps min/max in the Inspector, keep value 1 as the high root.
            if (pitchFloor > rootPitch)
            {
                float swap = pitchFloor;
                pitchFloor = rootPitch;
                rootPitch = swap;
            }

            // --- Amount → chromatic key index (0 = highest C / C8, 87 = lowest A / A0) ---
            int value = Mathf.RoundToInt(gemAmount);
            if (value < 1)
                value = 1;
            if (value > PianoKeyCount)
                value = PianoKeyCount;

            int keyIndex = value - 1;

            // --- True equal temperament from the root C ---
            // [TITAN-ORBIT] value 1 → root; value 2 (C#) → root × 2^(-1/12); value 13 (C) → root / 2.
            // Same multiply on root + floor = equal shift of the whole piano; intervals stay exact.
            float semitonesDown = SemitonesDownFromTopC(keyIndex);
            float pitch = rootPitch * Mathf.Pow(2f, -semitonesDown / 12f);

            // Floor only — do not re-stretch into [floor, root] (that was the uneven squash).
            if (pitch < pitchFloor)
                pitch = pitchFloor;

            return pitch;
        }

        /// <summary>
        /// Semitones <b>down</b> from the highest C for a chromatic key index (0..87).
        /// Value 1 → 0; value 2 (C#) → 1; value 13 (C one octave lower) → 12.
        /// </summary>
        /// <param name="keyIndex">Zero-based chromatic key index (value − 1).</param>
        /// <returns>Non-negative semitone distance below the top C.</returns>
        public static float SemitonesDownFromTopC(int keyIndex)
        {
            // --- Chromatic piano ---
            // [TITAN-ORBIT] Every gem-value step is one semitone (white + black keys).
            // Index 0 = C8; index 87 = A0 — the standard 88-key span.
            return Mathf.Max(0, keyIndex);
        }
    }
}
