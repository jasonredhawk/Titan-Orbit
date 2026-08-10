using Unity.Mathematics;

namespace TitanOrbit
{
    /// <summary>
    /// Splits a total gem amount into per-entity values whose chromatic pitches form a C-major chord.
    /// Used by asteroid destroy bursts (server) and multi-gem collect SFX (client) so explode → consume
    /// stays musically consistent.
    /// <para>
    /// [TITAN-ORBIT] Value N maps to the same chromatic piano as audio pitch
    /// (see gem musical pitch helper): semitone = (round(value) − 1) mod 12.
    /// Chord templates use chromatic degrees from C: 2 = root+fifth (C,G), 3 = major triad (C,E,G),
    /// 4 = major 7th (C,E,G,B), 5+ = triad stacked with extra octave copies. Exact sum is preserved;
    /// the last voice may absorb a few units of remainder so the economy stays exact.
    /// </para>
    /// </summary>
    public static class GemChordValues
    {
        /// <summary>Matches the musical piano-width cap (chromatic keys 1..88).</summary>
        public const int DefaultMaxUnitValue = 88;

        /// <summary>Semitones per octave (equal temperament). Value N+12 is the same note one octave lower.</summary>
        public const int NotesPerOctave = 12;

        /// <summary>Hard cap on burst / chord voices (matches gem explosion absolute max).</summary>
        public const int AbsoluteMaxVoices = 10;

        // --- Chord templates (chromatic semitone degrees from C within one octave) ---
        // [TITAN-ORBIT] Root+fifth power dyad when only two gems explode (C=0, G=7).
        static readonly int[] DegreesDyad = { 0, 7 };
        // Major triad — the default “pretty” asteroid dump (C=0, E=4, G=7).
        static readonly int[] DegreesTriad = { 0, 4, 7 };
        // Major 7th when four gems explode (C=0, E=4, G=7, B=11).
        static readonly int[] DegreesMaj7 = { 0, 4, 7, 11 };

        /// <summary>
        /// Writes <paramref name="count"/> gem values into <paramref name="values"/> that sum to
        /// <paramref name="remaining"/> and land on C-major chord tones when possible.
        /// </summary>
        /// <param name="remaining">Total asteroid leftover (or cargo delta) to split.</param>
        /// <param name="count">How many gem entities / chord voices (1..AbsoluteMaxVoices).</param>
        /// <param name="maxUnitValue">Per-gem cap (default 88). Each written value is clamped to this.</param>
        /// <param name="values">
        /// Destination length ≥ count. Only indices [0, count) are written.
        /// </param>
        public static void Fill(float remaining, int count, float maxUnitValue, float[] values)
        {
            // --- Guards ---
            if (values == null || count <= 0)
                return;

            count = math.clamp(count, 1, AbsoluteMaxVoices);
            if (count > values.Length)
                count = values.Length;

            float unit = math.max(1f, maxUnitValue);
            float total = math.max(0f, remaining);

            // One gem — no chord, just the full amount (clamped for safety).
            if (count == 1 || total < 0.001f)
            {
                values[0] = math.min(total, unit);
                for (int i = 1; i < count; i++)
                    values[i] = 0f;
                return;
            }

            // --- Pick chord template for this voice count ---
            GetDegreeTemplate(count, out int[] degrees, out int patternLen);

            // Target average value per voice — choose a base octave so notes sit near that register.
            float targetAvg = total / count;
            int baseOctave = (int)math.floor(math.max(0f, (targetAvg - 1f) / NotesPerOctave));
            int maxOctave = (int)math.floor((unit - 1f) / NotesPerOctave);
            baseOctave = math.clamp(baseOctave, 0, math.max(0, maxOctave));

            // --- Seed each voice on its chord degree (+ octave stacks for voices beyond the pattern) ---
            for (int i = 0; i < count; i++)
            {
                int degree = degrees[i % patternLen];
                int octaveStack = i / patternLen;
                int octave = baseOctave + octaveStack;
                if (octave > maxOctave)
                    octave = maxOctave;

                // value = octave*12 + degree + 1  →  (value-1) % 12 == degree
                float v = octave * NotesPerOctave + degree + 1;
                values[i] = math.clamp(v, 1f, unit);
            }

            // --- Match exact total while preferring ±12 steps (keeps chromatic chord degree) ---
            float sum = Sum(values, count);
            float diff = total - sum;
            const int maxAdjustIters = 64;
            for (int iter = 0; iter < maxAdjustIters && math.abs(diff) >= NotesPerOctave - 0.001f; iter++)
            {
                bool moved = false;
                if (diff > 0f)
                {
                    // Need more total — raise a voice by one octave if it still fits under the unit cap.
                    for (int i = 0; i < count; i++)
                    {
                        if (values[i] + NotesPerOctave <= unit + 0.001f)
                        {
                            values[i] += NotesPerOctave;
                            diff -= NotesPerOctave;
                            moved = true;
                            break;
                        }
                    }
                }
                else
                {
                    // Need less total — drop a voice by one octave if it stays a valid gem.
                    for (int i = 0; i < count; i++)
                    {
                        float minForDegree = (values[i] - 1f) % NotesPerOctave + 1f;
                        if (values[i] - NotesPerOctave >= minForDegree - 0.001f &&
                            values[i] - NotesPerOctave >= 1f)
                        {
                            values[i] -= NotesPerOctave;
                            diff += NotesPerOctave;
                            moved = true;
                            break;
                        }
                    }
                }

                if (!moved)
                    break;
            }

            // --- Exact sum: put leftover crumbs on the last voice (may leave the chord by a few steps) ---
            // [TITAN-ORBIT] Economy must sum exactly; a 1–11 unit nudge on the last note is worth it.
            sum = Sum(values, count);
            values[count - 1] += total - sum;

            // Keep last voice in [1, unit]; push overflow/underflow onto earlier voices if needed.
            if (values[count - 1] > unit)
            {
                float overflow = values[count - 1] - unit;
                values[count - 1] = unit;
                for (int i = 0; i < count - 1 && overflow > 0.001f; i++)
                {
                    float room = unit - values[i];
                    float add = math.min(room, overflow);
                    values[i] += add;
                    overflow -= add;
                }
            }
            else if (values[count - 1] < 1f && total >= 1f)
            {
                float need = 1f - values[count - 1];
                values[count - 1] = 1f;
                for (int i = 0; i < count - 1 && need > 0.001f; i++)
                {
                    float give = math.min(values[i] - 1f, need);
                    if (give <= 0f)
                        continue;
                    values[i] -= give;
                    need -= give;
                }
            }

            // Final safety clamp.
            for (int i = 0; i < count; i++)
                values[i] = math.clamp(values[i], 0f, unit);
        }

        /// <summary>
        /// How many chord voices to play for a cargo-gain SFX when gems may have been batched
        /// into one delta. Returns 1 for a single-ladder amount; 2+ when the total must have
        /// come from multiple unit-capped gems (amount &gt; max unit).
        /// </summary>
        /// <param name="amount">Cargo gems gained this frame.</param>
        /// <param name="maxUnitValue">Piano-width / sim unit cap (default 88).</param>
        public static int VoiceCountForCollect(float amount, float maxUnitValue = DefaultMaxUnitValue)
        {
            float unit = math.max(1f, maxUnitValue);
            if (amount <= unit + 0.001f)
                return 1;

            int voices = (int)math.ceil(amount / unit);
            return math.clamp(voices, 2, AbsoluteMaxVoices);
        }

        /// <summary>Selects the degree pattern for a given voice count.</summary>
        static void GetDegreeTemplate(int count, out int[] degrees, out int patternLen)
        {
            if (count <= 2)
            {
                degrees = DegreesDyad;
                patternLen = DegreesDyad.Length;
            }
            else if (count == 3)
            {
                degrees = DegreesTriad;
                patternLen = DegreesTriad.Length;
            }
            else if (count == 4)
            {
                degrees = DegreesMaj7;
                patternLen = DegreesMaj7.Length;
            }
            else
            {
                // 5+ voices: keep stacking the triad across octaves.
                degrees = DegreesTriad;
                patternLen = DegreesTriad.Length;
            }
        }

        static float Sum(float[] values, int count)
        {
            float s = 0f;
            for (int i = 0; i < count; i++)
                s += values[i];
            return s;
        }
    }
}
