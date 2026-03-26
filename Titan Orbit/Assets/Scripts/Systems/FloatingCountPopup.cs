using TMPro;
using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Types of floating (+N) popups.
    /// </summary>
    public enum FloatingCountType
    {
        Gems = 0,
        Damage = 1,
        Health = 2
    }

    /// <summary>
    /// Runtime-created world-space popup: rises upward, fades in, then fades out.
    /// </summary>
    public class FloatingCountPopup : MonoBehaviour
    {
        private TMP_Text tmpText;
        private SpriteRenderer iconRenderer;

        private Color baseColor = Color.white;
        private float elapsed;
        private float lifetime;
        private float riseSpeed;

        private void EnsureTextAndIcon()
        {
            if (tmpText == null)
            {
                var text3d = GetComponent<TextMeshPro>();
                if (text3d == null)
                    text3d = gameObject.AddComponent<TextMeshPro>();
                tmpText = text3d;
            }

            if (iconRenderer == null)
            {
                iconRenderer = GetComponent<SpriteRenderer>();
                if (iconRenderer == null)
                    iconRenderer = gameObject.AddComponent<SpriteRenderer>();
            }
        }

        public void Initialize(
            string message,
            Sprite iconSprite,
            Color color,
            TMP_FontAsset font,
            float fontSize,
            float duration,
            float riseSpeed,
            float iconScale,
            Vector3 iconLocalOffset
        )
        {
            EnsureTextAndIcon();

            if (tmpText == null)
            {
                Debug.LogWarning("FloatingCountPopup: TMP_Text missing; cannot initialize popup text.");
                Destroy(gameObject);
                return;
            }

            lifetime = Mathf.Max(0.1f, duration);
            this.riseSpeed = Mathf.Max(0f, riseSpeed);
            elapsed = 0f;

            baseColor = color;
            baseColor.a = 0f;

            // Text setup.
            tmpText.text = message;
            if (font != null)
                tmpText.font = font;
            tmpText.fontSize = fontSize;
            tmpText.transform.localScale = Vector3.one;
            tmpText.alignment = TextAlignmentOptions.Center;
            tmpText.enableWordWrapping = false;
            tmpText.richText = false;
            tmpText.color = baseColor;
            // Ensure TMP generates its first mesh immediately (avoids "invisible for a few frames" issues).
            tmpText.ForceMeshUpdate();

            // Icon setup.
            if (iconSprite != null)
            {
                if (iconRenderer != null)
                {
                    iconRenderer.sprite = iconSprite;
                    iconRenderer.color = baseColor;
                    iconRenderer.transform.localPosition = iconLocalOffset;
                    iconRenderer.transform.localScale = Vector3.one * Mathf.Max(0.0001f, iconScale);
                    iconRenderer.enabled = true;
                    iconRenderer.sortingOrder = 5000;
                }
            }
            else
            {
                if (iconRenderer != null)
                    iconRenderer.enabled = false;
            }
        }

        private void Update()
        {
            if (lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            // Move up over time.
            transform.position += Vector3.up * riseSpeed * Time.deltaTime;

            // Always face the main camera (billboard) so it's readable.
            var cam = UnityEngine.Camera.main;
            if (cam != null)
            {
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
            }

            // Fade in first half, fade out second half.
            float alpha = t <= 0.5f ? (t * 2f) : (1f - (t - 0.5f) * 2f);
            alpha = Mathf.Clamp01(alpha);

            Color c = baseColor;
            c.a = alpha;
            if (tmpText != null)
                tmpText.color = c;

            if (iconRenderer != null && iconRenderer.enabled)
                iconRenderer.color = c;

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }
    }
}

