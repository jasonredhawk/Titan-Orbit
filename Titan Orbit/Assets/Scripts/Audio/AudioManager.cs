using UnityEngine;
using UnityEngine.Audio;

namespace TitanOrbit.Audio
{
    /// <summary>
    /// Manages audio playback including background music and sound effects
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
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
        private const float WEAPON_PITCH_MIN = 0.5f;
        private const float WEAPON_PITCH_MAX = 2.5f;
        private const int GEM_SOUND_POOL_SIZE = 6;
        private const float GEM_PITCH_MIN = 0.55f;
        private const float GEM_PITCH_MAX = 1.9f;
        private const float GEM_AMOUNT_MIN = 1f;
        private const float GEM_AMOUNT_MAX = 300f;
        private const int IMPACT_SOUND_POOL_SIZE = 6;
        private const float IMPACT_PITCH_MIN = 0.55f;
        private const float IMPACT_PITCH_MAX = 1.9f;

        [Header("Audio Clips")]
        [SerializeField] private AudioClip backgroundMusic;
        [Tooltip("Weapon fire (one shot per cannon, pitch varies by bullet size/speed). Assign e.g. laser_01 from ShootingSound folder.")]
        [SerializeField] private AudioClip shootSound;
        [Tooltip("Collision and impact (ship-asteroid, bullet hit). Assign cannon_01 from ShootingSound folder.")]
        [SerializeField] private AudioClip impactSound;
        [Tooltip("Gem pickup. Assign magic_03 from ShootingSound folder.")]
        [SerializeField] private AudioClip gemCollectSound;
        [Tooltip("People transfer SFX used for both loading and unloading.")]
        [SerializeField] private AudioClip peopleTransferSound;
        [SerializeField] private AudioClip miningSound;
        [SerializeField] private AudioClip captureSound;
        [SerializeField] private AudioClip explosionSound;
        [SerializeField] private AudioClip upgradeSound;

        [Header("Settings")]
        [SerializeField] private float musicVolume = 0.7f;
        [SerializeField] private float sfxVolume = 1f;
        [SerializeField] private bool playMusicOnStart = true;

        private void Awake()
        {
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
            if (musicSource != null && backgroundMusic != null)
            {
                musicSource.clip = backgroundMusic;
                musicSource.volume = musicVolume;
                musicSource.Play();
            }
        }

        public void PlayShootSound()
        {
            PlaySFX(shootSound);
        }

        /// <summary>
        /// Play weapon fire sound with pitch derived from bullet size and speed.
        /// Bigger bullet = lower pitch (deeper); faster bullet = higher pitch (shorter playback).
        /// Call once per weapon/cannon that fired.
        /// </summary>
        /// <param name="pitch">Pitch multiplier. Clamped to 0.5–2.5. Higher = higher tone and shorter length.</param>
        public void PlayWeaponShootSound(float pitch)
        {
            if (shootSound == null) return;
            EnsureWeaponSoundPool();
            if (weaponSoundSources == null || weaponSoundSources.Length == 0) { PlaySFX(shootSound); return; }
            float p = Mathf.Clamp(pitch, WEAPON_PITCH_MIN, WEAPON_PITCH_MAX);
            AudioSource src = weaponSoundSources[nextWeaponSoundIndex % weaponSoundSources.Length];
            nextWeaponSoundIndex = (nextWeaponSoundIndex + 1) % weaponSoundSources.Length;
            if (src != null)
            {
                src.pitch = p;
                src.PlayOneShot(shootSound, sfxVolume);
            }
        }

        private void EnsureWeaponSoundPool()
        {
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
            if (impactSound == null) return;
            EnsureImpactSoundPool();
            if (impactSoundSources == null || impactSoundSources.Length == 0)
            {
                PlaySFX(impactSound);
                return;
            }

            float p = Mathf.Clamp(pitch, IMPACT_PITCH_MIN, IMPACT_PITCH_MAX);
            AudioSource src = impactSoundSources[nextImpactSoundIndex % impactSoundSources.Length];
            nextImpactSoundIndex = (nextImpactSoundIndex + 1) % impactSoundSources.Length;
            if (src != null)
            {
                src.pitch = p;
                src.PlayOneShot(impactSound, sfxVolume);
            }
        }

        public void PlayGemCollectSound()
        {
            PlaySFX(gemCollectSound);
        }

        public void PlayGemCollectSound(float amount)
        {
            PlayGemValueScaledSFX(gemCollectSound, amount);
        }

        public void PlayMiningSound()
        {
            PlaySFX(miningSound);
        }

        public void PlayPeopleLoadSound(float amount)
        {
            PlayPeopleTransferSound(amount, true);
        }

        public void PlayPeopleUnloadSound(float amount)
        {
            PlayPeopleTransferSound(amount, false);
        }

        public void PlayCaptureSound()
        {
            PlaySFX(captureSound);
        }

        public void PlayGemDepositSound(float amount)
        {
            PlayGemValueScaledSFX(gemCollectSound, amount);
        }

        public void PlayExplosionSound()
        {
            PlaySFX(explosionSound);
        }

        public void PlayUpgradeSound()
        {
            PlaySFX(upgradeSound);
        }

        private void PlaySFX(AudioClip clip)
        {
            if (sfxSource != null && clip != null)
            {
                sfxSource.PlayOneShot(clip, sfxVolume);
            }
        }

        private void PlayGemValueScaledSFX(AudioClip clip, float amount)
        {
            if (clip == null) return;
            EnsureGemSoundPool();
            if (gemSoundSources == null || gemSoundSources.Length == 0)
            {
                PlaySFX(clip);
                return;
            }

            float clampedAmount = Mathf.Clamp(Mathf.Max(0.001f, amount), GEM_AMOUNT_MIN, GEM_AMOUNT_MAX);
            // Log mapping gives stronger audible contrast across small-to-large gem values.
            float minLog = Mathf.Log10(GEM_AMOUNT_MIN);
            float maxLog = Mathf.Log10(GEM_AMOUNT_MAX);
            float amountLog = Mathf.Log10(clampedAmount);
            float normalized = Mathf.InverseLerp(minLog, maxLog, amountLog);
            float pitch = Mathf.Lerp(GEM_PITCH_MAX, GEM_PITCH_MIN, normalized);
            AudioSource src = gemSoundSources[nextGemSoundIndex % gemSoundSources.Length];
            nextGemSoundIndex = (nextGemSoundIndex + 1) % gemSoundSources.Length;
            if (src != null)
            {
                src.pitch = pitch;
                src.PlayOneShot(clip, sfxVolume);
            }
        }

        private void PlayPeopleTransferSound(float amount, bool isLoad)
        {
            if (peopleTransferSound == null) return;
            EnsureGemSoundPool();
            if (gemSoundSources == null || gemSoundSources.Length == 0)
            {
                PlaySFX(peopleTransferSound);
                return;
            }

            float normalized = Mathf.InverseLerp(1f, 10f, Mathf.Max(0f, amount));
            float basePitch = isLoad ? 1.12f : 0.92f;
            float amountPitchOffset = Mathf.Lerp(0.16f, -0.12f, normalized);
            float pitch = Mathf.Clamp(basePitch + amountPitchOffset, 0.65f, 1.5f);

            AudioSource src = gemSoundSources[nextGemSoundIndex % gemSoundSources.Length];
            nextGemSoundIndex = (nextGemSoundIndex + 1) % gemSoundSources.Length;
            if (src != null)
            {
                src.pitch = pitch;
                src.PlayOneShot(peopleTransferSound, sfxVolume);
            }
        }

        private void EnsureGemSoundPool()
        {
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
            if (Application.isMobilePlatform)
            {
                // Reduce audio quality for mobile
                AudioSettings.SetDSPBufferSize(256, 4);
            }
        }
    }
}
