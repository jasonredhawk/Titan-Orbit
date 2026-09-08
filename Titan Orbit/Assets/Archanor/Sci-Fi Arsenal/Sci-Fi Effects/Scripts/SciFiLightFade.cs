using UnityEngine;

namespace SciFiArsenal
{
    public class SciFiLightFade : MonoBehaviour
    {
        [Header("Seconds to dim the light")]
        public float life = 0.2f;
        public bool killAfterLife = true;

        Light li;
        float initIntensity;

        void Awake()
        {
            // Cache once. GetComponent<Light>() every Update allocated in the Editor
            // and stayed alive on pooled muzzle/impact shells after VfxUrpCompat
            // stripped the Light (profiler: SciFiLightFade ~10 KB/frame while firing).
            li = GetComponent<Light>();
            if (li == null)
            {
                enabled = false;
                return;
            }

            initIntensity = li.intensity;
        }

        void Update()
        {
            if (li == null)
            {
                enabled = false;
                return;
            }

            float duration = life > 1e-5f ? life : 1e-5f;
            li.intensity -= initIntensity * (Time.deltaTime / duration);
            if (killAfterLife && li.intensity <= 0f)
            {
                // Disable only — Destroy broke BulletOneShotVfxPool shells (child Light gone).
                li.enabled = false;
                enabled = false;
            }
        }
    }
}