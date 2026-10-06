using UnityEngine;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Maps gem cargo / deposit amounts onto a full chromatic piano for SFX pitch
    /// (all 88 keys: white and black / semitones). Shared by deposit metronome and gem
    /// collect (asteroid pickup) so both use the same clip language. Weapon muzzle
    /// and bullet-impact pitch use the same ladder after
    /// <see cref="FirePowerToPianoAmount"/> — each gun / cannon / rocket / sniper
    /// treats <b>its authored base</b> as the top C, then walks down toward
    /// <c>base + perExtra × max Extra Levels</c>.
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
        /// Keys on one weapon-type piano (two octaves). Base fire power = key 1
        /// (top C). Max Extra Level fire power = key 24 (two octaves down).
        /// </summary>
        public const int WeaponTypePianoKeyCount = 24;

        /// <summary>
        /// Family upgrade-tree cap used for the weapon-pitch ceiling.
        /// Extra Level steps = <c>shipLevel + Fire Power purchases</c>, and
        /// purchases cap at ship level, so the ceiling is 6+6 = 12.
        /// MEGA unique weapons have PerExtra 0 — they stay on the top C.
        /// </summary>
        public const int WeaponPianoMaxShipLevel = 6;

        /// <summary>
        /// <c>CountWeaponExtraLevels(6, 6)</c> — max Extra Level steps a family
        /// gun / cannon can apply. Pitch max = base + perExtra × this.
        /// </summary>
        public const int MaxWeaponExtraLevelSteps = WeaponPianoMaxShipLevel * 2;

        /// <summary>
        /// Raw fire power where shoot-volume boost begins. Machine guns stay at 1.
        /// Titan unique-component cannons (~50) sit at the loud end.
        /// </summary>
        public const float ShootVolumeBoostStartsAtFirePower = 14f;

        /// <summary>
        /// Fire power that reaches <see cref="DefaultHeavyShootVolumeMul"/>.
        /// Matches authored Titan unique-component cannon damage — volume only,
        /// not pitch.
        /// </summary>
        public const float HeavyShootVolumeAtFirePower = 50f;

        /// <summary>
        /// Default PlayOneShot multiplier at <see cref="HeavyShootVolumeAtFirePower"/>.
        /// Cannons fire slowly; a bit more level keeps the shared clip audible.
        /// </summary>
        public const float DefaultHeavyShootVolumeMul = 3.25f;

        /// <summary>
        /// Converts a gem or weapon-piano amount into an <see cref="AudioSource.pitch"/>
        /// multiplier on the chromatic piano ladder.
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

        /// <summary>
        /// Fallback when Extra Level bookends are unknown (PD / drones / old callers).
        /// Treats live damage as the type base so the shot sits on the top C.
        /// </summary>
        public static float FirePowerToPianoAmount(float firePower) =>
            FirePowerToPianoAmount(firePower, firePower, 0f);

        /// <summary>
        /// Per-weapon-type piano: authored <paramref name="baseFirePower"/> is the
        /// top C (key 1). The low end is
        /// <c>base + perExtra × <see cref="MaxWeaponExtraLevelSteps"/></c>
        /// (L6 ship + L6 Fire Power purchases). Live Extra Level fire power
        /// InverseLerps that span onto <see cref="WeaponTypePianoKeyCount"/> keys.
        /// MEGA unique guns / cannons / rockets / snipers author PerExtra 0, so
        /// they stay on the top C. Bank damage multipliers are ignored — pass
        /// mount Extra Level fire power, not bank-scaled <c>plan.Damage</c>.
        /// </summary>
        /// <param name="liveFirePower">Current Extra Level fire power (pre-bank).</param>
        /// <param name="baseFirePower">Catalog / unique-component base (top C).</param>
        /// <param name="firePowerPerExtraLevel">Catalog Per Extra Level step.</param>
        /// <returns>Piano amount for <see cref="ResolvePitch"/> (1…24).</returns>
        public static float FirePowerToPianoAmount(
            float liveFirePower,
            float baseFirePower,
            float firePowerPerExtraLevel)
        {
            // --- Unknown bookends → top C ---
            // [TITAN-ORBIT] Planetary defense / drones do not carry Extra Level
            // bookends. Using live as base keeps them on the high root.
            float live = Mathf.Max(0.25f, liveFirePower);
            float bas = baseFirePower > 0.01f ? baseFirePower : live;
            float per = Mathf.Max(0f, firePowerPerExtraLevel);
            float max = bas + per * MaxWeaponExtraLevelSteps;
            if (max <= bas + 0.001f)
                return 1f;

            // --- Base = top C; max Extra Level = two octaves down ---
            float t = Mathf.InverseLerp(bas, max, live);
            return 1f + Mathf.Clamp01(t) * (WeaponTypePianoKeyCount - 1f);
        }

        /// <summary>
        /// Extra PlayOneShot volume for heavier shots. Guns and cannons share one
        /// clip. Volume follows raw fire power so a slow 50-FP cannon is louder
        /// than a machine-gun tick; pitch uses the per-weapon Extra Level piano.
        /// </summary>
        /// <param name="firePower">Per-shot damage / fire power (not hull-sum DPS).</param>
        /// <param name="heavyMul">Volume at <see cref="HeavyShootVolumeAtFirePower"/>. Must be ≥ 1.</param>
        /// <returns>Multiplier on top of <c>AudioManager</c> shoot mix (1…heavyMul).</returns>
        public static float FirePowerToShootVolumeScale(
            float firePower,
            float heavyMul = DefaultHeavyShootVolumeMul)
        {
            // --- Loud cannons, quiet guns ---
            // [TITAN-ORBIT] Pitch is per-weapon Extra Level. Volume still rises
            // with raw damage so a 0.5/s Titan cannon is not lost next to chatter.
            float t = Mathf.InverseLerp(
                ShootVolumeBoostStartsAtFirePower, HeavyShootVolumeAtFirePower, firePower);
            return Mathf.Lerp(1f, Mathf.Max(1f, heavyMul), Mathf.Clamp01(t));
        }
    }
}
