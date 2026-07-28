using UnityEngine;
using UnityEngine.Audio;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Central client audio hub for music and gameplay SFX.
    /// Owns pooled <see cref="AudioSource"/>s for weapons, gems, and impacts so overlapping
    /// one-shots can use different pitches without fighting a single source.
    /// Gem deposit and gem collect share <see cref="GemMusicalPitch"/> (chromatic 88-key piano)
    /// and the same <see cref="gemCollectSound"/> clip — only volume / proximity differ.
    /// Multi-gem collect batches play a C-major chord via <see cref="GemChordValues"/>.
    /// Singleton with DontDestroyOnLoad — UI and hybrid presenters call into <see cref="Instance"/>.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        /// <summary>Scene-wide singleton set in Awake; null after teardown / duplicate destroy.</summary>
        public static AudioManager Instance { get; private set; }

        [Header("Audio Mixer")]
        [SerializeField] private AudioMixer audioMixer;
        [SerializeField] private AudioMixerGroup musicGroup;
        [SerializeField] private AudioMixerGroup sfxGroup;

        [Header("Audio Sources")]
        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioSource sfxSource;
        [Tooltip("Optional pool for weapon fire (one sound per cannon with different pitch). If empty, created at runtime.")]
        [SerializeField] private AudioSource[] weaponSoundSources;
        private int nextWeaponSoundIndex;
        [Tooltip("Optional pool for value-based gem sounds (pickup/deposit) with varying pitch.")]
        [SerializeField] private AudioSource[] gemSoundSources;
        private int nextGemSoundIndex;
        [Tooltip("Optional pool for value-based impact sounds with varying pitch.")]
        [SerializeField] private AudioSource[] impactSoundSources;
        private int nextImpactSoundIndex;

        private const int WEAPON_SOUND_POOL_SIZE = 6;
        /// <summary>Large enough for a 3–4 note collect chord plus overlapping sequential pickups.</summary>
        private const int GEM_SOUND_POOL_SIZE = 10;
        private const int IMPACT_SOUND_POOL_SIZE = 6;
        private const float IMPACT_PITCH_MIN = 0.3f;
        private const float IMPACT_PITCH_MAX = 2.4f;

        [Header("Audio Clips")]
        [SerializeField] private AudioClip backgroundMusic;
        [Tooltip("Weapon fire (one shot per cannon, pitch varies by bullet size/speed). Assign e.g. laser_01 from ShootingSound folder.")]
        [SerializeField] private AudioClip shootSound;
        [Tooltip("Collision and impact (ship-asteroid, bullet hit). Assign cannon_01 from ShootingSound folder.")]
        [SerializeField] private AudioClip impactSound;
        [Tooltip("Asteroid collision impact. Falls back to Impact Sound when unassigned.")]
        [SerializeField] private AudioClip asteroidCollisionSound;
        [Tooltip("Ship-to-ship collision impact. Falls back to Impact Sound when unassigned.")]
        [SerializeField] private AudioClip shipCollisionSound;
        [Tooltip("Gem pickup. Assign magic_03 from ShootingSound folder.")]
        [SerializeField] private AudioClip gemCollectSound;
        [Tooltip("People transfer SFX used for both loading and unloading.")]
        [SerializeField] private AudioClip peopleTransferSound;
        [SerializeField] private AudioClip miningSound;
        [SerializeField] private AudioClip captureSound;
        [Tooltip("Large boom when the ship (or other) explosion VFX spawns.")]
        [SerializeField] private AudioClip explosionSound;
        [Tooltip("Hull break / death sting when a ship is destroyed (plays with breakup VFX).")]
        [SerializeField] private AudioClip shipDeathSound;
        [SerializeField] private AudioClip upgradeSound;

        [Header("Settings")]
        [SerializeField] private float musicVolume = 0.7f;
        [SerializeField] private float sfxVolume = 1f;
        [Header("SFX Mix")]
        [SerializeField] private float shootVolume = 1f;
        [SerializeField] private float impactVolume = 1f;
        [SerializeField] private float asteroidCollisionVolume = 1f;
        [SerializeField] private float shipCollisionVolume = 1f;
        [SerializeField] private float gemVolume = 1f;
        [SerializeField] private float peopleVolume = 1f;
        [SerializeField] private float miningVolume = 1f;
        [SerializeField] private float captureVolume = 1f;
        [SerializeField] private float explosionVolume = 1f;
        [SerializeField] private float shipDeathVolume = 1f;
        [SerializeField] private float upgradeVolume = 1f;
        [SerializeField] private bool playMusicOnStart = true;

        [Header("Pitch ranges (SFX)")]
        [Tooltip("Weapon fire pitch clamp. Bigger bullet / faster shot uses values in this range.")]
        [SerializeField] private float weaponPitchMin = 0.01f;
        [SerializeField] private float weaponPitchMax = 1f;
        [Tooltip("Pitch floor for very low piano keys. Shift this by the SAME factor as Gem Pitch Max (e.g. both ×1.5) so chromatic intervals stay true.")]
        [SerializeField] private float gemPitchMin = 0.15f;
        [Tooltip("Pitch at gem value 1 (highest C / ET root). Value 13 = this÷2 (one octave). Unity AudioClip clamps at 3 — default is that ceiling.")]
        [SerializeField] private float gemPitchMax = 3f;
        [Tooltip("People load/unload pitch clamp after base pitch and amount offset.")]
        [SerializeField] private float peoplePitchMin = 0.01f;
        [SerializeField] private float peoplePitchMax = 1f;

        private void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            // --- Unity lifecycle ---
            if (musicSource == null)
            {
                musicSource = gameObject.AddComponent<AudioSource>();
                musicSource.loop = true;
                musicSource.outputAudioMixerGroup = musicGroup;
            }

            if (sfxSource == null)
            {
                sfxSource = gameObject.AddComponent<AudioSource>();
                sfxSource.outputAudioMixerGroup = sfxGroup;
            }

            EnsureWeaponSoundPool();
            EnsureGemSoundPool();
            EnsureImpactSoundPool();

            if (playMusicOnStart && backgroundMusic != null)
            {
                PlayBackgroundMusic();
            }
        }

        public void PlayBackgroundMusic()
        {
            // --- PlayBackgroundMusic ---
            if (musicSource != null && backgroundMusic != null)
            {
                musicSource.clip = backgroundMusic;
                musicSource.volume = musicVolume;
                musicSource.Play();
            }
        }

        public void PlayShootSound()
        {
            PlaySFX(shootSound, shootVolume);
        }

        /// <summary>
        /// Play weapon fire sound with pitch derived from bullet size and speed.
        /// Bigger bullet = lower pitch (deeper); faster bullet = higher pitch (shorter playback).
        /// Call once per weapon/cannon that fired.
        /// </summary>
        /// <param name="pitch">Pitch multiplier. Clamped to 0.5–2.5. Higher = higher tone and shorter length.</param>
        public void PlayWeaponShootSound(float pitch)
        {
            // --- PlayWeaponShootSound ---
            if (shootSound == null) return;
            EnsureWeaponSoundPool();
            if (weaponSoundSources == null || weaponSoundSources.Length == 0) { PlaySFX(shootSound); return; }
            float p = Mathf.Clamp(pitch, weaponPitchMin, weaponPitchMax);
            AudioSource src = weaponSoundSources[nextWeaponSoundIndex % weaponSoundSources.Length];
            nextWeaponSoundIndex = (nextWeaponSoundIndex + 1) % weaponSoundSources.Length;
            if (src != null)
            {
                src.pitch = p;
                src.PlayOneShot(shootSound, GetSFXVolume(shootVolume));
            }
        }

        private void EnsureWeaponSoundPool()
        {
            // --- Ensure setup ---
            if (weaponSoundSources != null && weaponSoundSources.Length > 0) return;
            weaponSoundSources = new AudioSource[WEAPON_SOUND_POOL_SIZE];
            for (int i = 0; i < WEAPON_SOUND_POOL_SIZE; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.outputAudioMixerGroup = sfxGroup;
                weaponSoundSources[i] = src;
            }
        }

        public void PlayImpactSound()
        {
            PlayImpactSound(1f);
        }

        public void PlayImpactSound(float pitch)
        {
            PlayPooledImpactSound(impactSound, impactVolume, pitch);
        }

        public void PlayAsteroidCollisionSound()
        {
            PlayAsteroidCollisionSound(1f);
        }

        public void PlayAsteroidCollisionSound(float pitch)
        {
            AudioClip clip = asteroidCollisionSound != null ? asteroidCollisionSound : impactSound;
            PlayPooledImpactSound(clip, asteroidCollisionVolume, pitch);
        }

        public void PlayShipCollisionSound()
        {
            PlayShipCollisionSound(1f);
        }

        public void PlayShipCollisionSound(float pitch)
        {
            AudioClip clip = shipCollisionSound != null ? shipCollisionSound : impactSound;
            PlayPooledImpactSound(clip, shipCollisionVolume, pitch);
        }

        private void PlayPooledImpactSound(AudioClip clip, float clipVolumeMultiplier, float pitch)
        {
            // --- PlayPooledImpactSound ---
            if (clip == null) return;
            EnsureImpactSoundPool();
            if (impactSoundSources == null || impactSoundSources.Length == 0)
            {
                PlaySFX(clip, clipVolumeMultiplier);
                return;
            }

            float p = Mathf.Clamp(pitch, IMPACT_PITCH_MIN, IMPACT_PITCH_MAX);
            AudioSource src = impactSoundSources[nextImpactSoundIndex % impactSoundSources.Length];
            nextImpactSoundIndex = (nextImpactSoundIndex + 1) % impactSoundSources.Length;
            if (src != null)
            {
                src.pitch = p;
                src.PlayOneShot(clip, GetSFXVolume(clipVolumeMultiplier));
            }
        }

        /// <summary>
        /// Gem collect with no amount — plays at value-1 pitch (highest C).
        /// Prefer <see cref="PlayGemCollectSound(float)"/> when cargo delta is known.
        /// </summary>
        public void PlayGemCollectSound()
        {
            PlayGemMusicalSFX(gemCollectSound, 1f, gemVolume, 1f);
        }

        /// <summary>
        /// Gem collect / consume SFX when cargo rises (asteroid gems, mine pickups).
        /// Pitch uses the same chromatic piano ladder as deposit (<see cref="GemMusicalPitch"/>).
        /// When the cargo jump is larger than one piano-width unit (batched multi-gem pickup),
        /// plays a C-major chord via <see cref="GemChordValues"/> so it matches explode splits.
        /// </summary>
        /// <param name="amount">Cargo gems gained this frame (drives which piano key / chord).</param>
        public void PlayGemCollectSound(float amount)
        {
            // --- Single note vs chord ---
            // [TITAN-ORBIT] Individual gem pickups (amount ≤ 88) keep one pitch — burst gems are
            // already chord-toned, so scooping them in sequence layers a chord in the pool.
            // A single ghost frame that batches several large gems (amount > 88) reconstructs the chord.
            int voices = GemChordValues.VoiceCountForCollect(amount, GemChordValues.DefaultMaxUnitValue);
            if (voices <= 1)
            {
                PlayGemMusicalSFX(gemCollectSound, amount, gemVolume, 1f);
                return;
            }

            var chordValues = new float[voices];
            GemChordValues.Fill(amount, voices, GemChordValues.DefaultMaxUnitValue, chordValues);

            // Slightly ease volume per voice so a triad does not triple the loudness.
            float voiceVolumeScale = 1f / Mathf.Sqrt(voices);
            for (int i = 0; i < voices; i++)
            {
                if (chordValues[i] < 0.001f)
                    continue;
                PlayGemMusicalSFX(gemCollectSound, chordValues[i], gemVolume, voiceVolumeScale);
            }
        }

        public void PlayMiningSound()
        {
            PlaySFX(miningSound, miningVolume);
        }

        /// <summary>
        /// People-load transfer sting (planet → ship delivery). Pitch uses a slightly higher
        /// base than unload, then shifts down as <paramref name="amount"/> (N) grows.
        /// Called from <c>PeopleTransportVfxDriver</c> on Consumed — not from server sim.
        /// </summary>
        /// <param name="amount">People transferred (N). Mapped 1…10 → pitch offset.</param>
        public void PlayPeopleLoadSound(float amount)
        {
            PlayPeopleTransferSound(amount, true);
        }

        /// <summary>
        /// People-unload transfer sting (ship → planet delivery). Lower base pitch than load;
        /// larger N still pushes pitch down within <see cref="peoplePitchMin"/>…<see cref="peoplePitchMax"/>.
        /// </summary>
        /// <param name="amount">People transferred (N). Mapped 1…10 → pitch offset.</param>
        public void PlayPeopleUnloadSound(float amount)
        {
            PlayPeopleTransferSound(amount, false);
        }

        public void PlayCaptureSound()
        {
            PlaySFX(captureSound, captureVolume);
        }

        /// <summary>
        /// Resolves the live singleton even if Awake order left <see cref="Instance"/> null
        /// (player builds can destroy/recreate boot scenes differently than the Editor).
        /// </summary>
        public static AudioManager GetOrFind()
        {
            if (Instance != null)
                return Instance;
            Instance = FindFirstObjectByType<AudioManager>();
            return Instance;
        }

        /// <summary>
        /// Gem-deposit metronome beat. Pitch follows <paramref name="gemValue"/> on the shared
        /// chromatic piano ladder (same math as collect). Leftover cargo uses the smaller chunk
        /// so the last ticks are not a fake full-load pitch. Optional <paramref name="volumeScale"/>
        /// applies proximity falloff for other players' deposits (1 = full, 0 = silent).
        /// </summary>
        /// <param name="gemValue">Actual gem chunk for pitch this beat.</param>
        /// <param name="volumeScale">Extra multiplier after mix settings — used for distance hear range.</param>
        public void PlayGemDepositSound(float gemValue, float volumeScale = 1f)
        {
            // --- Deposit metronome one-shot ---
            // [TITAN-ORBIT] Same musical pitch as PlayGemCollectSound — gemPitchMax = value-1 C root
            // (true ET; value 13 = half pitch). gemPitchMin is a floor only. Leftover loads pitch
            // as the smaller chunk (e.g. 3 gems on a level-5 ship → pitch as 3).
            PlayGemMusicalSFX(gemCollectSound, gemValue, gemVolume, volumeScale);
        }

        public void PlayExplosionSound()
        {
            PlaySFX(explosionSound, explosionVolume);
        }

        public void PlayShipDeathSound()
        {
            PlaySFX(shipDeathSound, shipDeathVolume);
        }

        public void PlayUpgradeSound()
        {
            PlaySFX(upgradeSound, upgradeVolume);
        }

        private void PlaySFX(AudioClip clip)
        {
            PlaySFX(clip, 1f);
        }

        private void PlaySFX(AudioClip clip, float clipVolumeMultiplier)
        {
            // --- PlaySFX ---
            if (sfxSource != null && clip != null)
            {
                sfxSource.PlayOneShot(clip, GetSFXVolume(clipVolumeMultiplier));
            }
        }

        /// <summary>
        /// Plays a gem one-shot on the shared chromatic piano ladder.
        /// Used by both deposit metronome and collect / consume SFX.
        /// </summary>
        /// <param name="clip">Usually <see cref="gemCollectSound"/>.</param>
        /// <param name="gemAmount">Gem value → piano key (1 = highest C, 88 = lowest A).</param>
        /// <param name="clipVolumeMultiplier">Mix slider for gems before global SFX volume.</param>
        /// <param name="volumeScale">Extra 0–1 scale (remote deposit proximity).</param>
        private void PlayGemMusicalSFX(
            AudioClip clip,
            float gemAmount,
            float clipVolumeMultiplier,
            float volumeScale)
        {
            // --- Musical gem one-shot ---
            if (clip == null || volumeScale <= 0.001f)
                return;

            // Ensure sources exist even if Start() has not run yet (first deposit beat mid-frame).
            if (sfxSource == null)
            {
                sfxSource = gameObject.AddComponent<AudioSource>();
                sfxSource.playOnAwake = false;
                sfxSource.outputAudioMixerGroup = sfxGroup;
            }

            EnsureGemSoundPool();
            float volume = GetSFXVolume(clipVolumeMultiplier * Mathf.Clamp01(volumeScale));
            if (volume <= 0.001f)
                return;

            // [TITAN-ORBIT] ET from gemPitchMax (root C); gemPitchMin = floor — see GemMusicalPitch.
            float pitch = GemMusicalPitch.ResolvePitch(gemAmount, gemPitchMax, gemPitchMin);

            if (gemSoundSources == null || gemSoundSources.Length == 0)
            {
                sfxSource.pitch = pitch;
                sfxSource.PlayOneShot(clip, volume);
                sfxSource.pitch = 1f;
                return;
            }

            // Round-robin pool so overlapping gem SFX keep their own pitch on separate sources.
            AudioSource src = gemSoundSources[nextGemSoundIndex % gemSoundSources.Length];
            nextGemSoundIndex = (nextGemSoundIndex + 1) % gemSoundSources.Length;
            if (src == null)
            {
                sfxSource.pitch = pitch;
                sfxSource.PlayOneShot(clip, volume);
                sfxSource.pitch = 1f;
                return;
            }

            src.pitch = pitch;
            src.PlayOneShot(clip, volume);
        }

        /// <summary>
        /// Shared people load/unload one-shot. Uses the gem pool so overlapping transfers keep
        /// their own pitch. Original NGO formula: load base 1.12 / unload 0.92, then
        /// InverseLerp(1,10,N) → offset +0.16…−0.12 (bigger N = lower pitch).
        /// </summary>
        /// <param name="amount">People count N for pitch scaling.</param>
        /// <param name="isLoad">True = load (planet→ship), false = unload (ship→planet).</param>
        private void PlayPeopleTransferSound(float amount, bool isLoad)
        {
            // --- People transfer one-shot (N-scaled pitch) ---
            if (peopleTransferSound == null)
                return;

            EnsureGemSoundPool();
            if (gemSoundSources == null || gemSoundSources.Length == 0)
            {
                // Pool not ready — still play the clip without custom pitch.
                PlaySFX(peopleTransferSound, peopleVolume);
                return;
            }

            // [TITAN-ORBIT] N 1 → highest offset, N ≥ 10 → lowest. Load sits above unload.
            float normalized = Mathf.InverseLerp(1f, 10f, Mathf.Max(0f, amount));
            float basePitch = isLoad ? 1.12f : 0.92f;
            float amountPitchOffset = Mathf.Lerp(0.16f, -0.12f, normalized);
            float pitch = Mathf.Clamp(basePitch + amountPitchOffset, peoplePitchMin, peoplePitchMax);

            // Round-robin so two arrivals in one frame do not overwrite each other's pitch.
            AudioSource src = gemSoundSources[nextGemSoundIndex % gemSoundSources.Length];
            nextGemSoundIndex = (nextGemSoundIndex + 1) % gemSoundSources.Length;
            if (src != null)
            {
                src.pitch = pitch;
                src.PlayOneShot(peopleTransferSound, GetSFXVolume(peopleVolume));
            }
        }

        private float GetSFXVolume(float clipVolumeMultiplier)
        {
            return Mathf.Max(0f, sfxVolume * clipVolumeMultiplier);
        }

        private void EnsureGemSoundPool()
        {
            // --- Ensure setup ---
            if (gemSoundSources != null && gemSoundSources.Length > 0) return;
            gemSoundSources = new AudioSource[GEM_SOUND_POOL_SIZE];
            for (int i = 0; i < GEM_SOUND_POOL_SIZE; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.outputAudioMixerGroup = sfxGroup;
                gemSoundSources[i] = src;
            }
        }

        private void EnsureImpactSoundPool()
        {
            // --- Ensure setup ---
            if (impactSoundSources != null && impactSoundSources.Length > 0) return;
            impactSoundSources = new AudioSource[IMPACT_SOUND_POOL_SIZE];
            for (int i = 0; i < IMPACT_SOUND_POOL_SIZE; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.outputAudioMixerGroup = sfxGroup;
                impactSoundSources[i] = src;
            }
        }

        public void SetMusicVolume(float volume)
        {
            // --- SetMusicVolume ---
            musicVolume = Mathf.Clamp01(volume);
            if (musicSource != null)
            {
                musicSource.volume = musicVolume;
            }
        }

        public void SetSFXVolume(float volume)
        {
            sfxVolume = Mathf.Clamp01(volume);
        }

        // Mobile optimization: reduce audio quality on mobile
        private void OnEnable()
        {
            // --- Unity lifecycle ---
            if (Application.isMobilePlatform)
            {
                // Reduce audio quality for mobile
                AudioSettings.SetDSPBufferSize(256, 4);
            }
        }
    }
}
