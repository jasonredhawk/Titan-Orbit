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

        [Header("Audio Clips")]
        [SerializeField] private AudioClip backgroundMusic;
        [SerializeField] private AudioClip shootSound;
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

        public void PlayMiningSound()
        {
            PlaySFX(miningSound);
        }

        public void PlayCaptureSound()
        {
            PlaySFX(captureSound);
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
