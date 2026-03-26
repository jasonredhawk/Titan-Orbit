using TMPro;
using UnityEngine;

namespace TitanOrbit.Systems
{
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
            // Minimum drift so a zero/missing inspector value never looks "stuck".
            this.riseSpeed = Mathf.Max(0.15f, riseSpeed);
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

        /// <summary>
        /// Direction that reads as "up" on screen but stays on the XZ play plane (Y locked to 0).
        /// World +Y is nearly invisible from a top-down camera, so we use camera screen-up flattened to XZ.
        /// </summary>
        private static Vector3 GetRiseDirectionOnPlayPlane(UnityEngine.Camera cam)
        {
            if (cam == null)
                return Vector3.forward;

            Vector3 dir = cam.transform.up;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.ProjectOnPlane(-cam.transform.forward, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            return dir.normalized;
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

            var cam = UnityEngine.Camera.main;
            // Drift along screen-up on the ground plane (not world Y).
            transform.position += GetRiseDirectionOnPlayPlane(cam) * riseSpeed * Time.deltaTime;
            Vector3 pos = transform.position;
            pos.y = 0f;
            transform.position = pos;

            // Always face the main camera (billboard) so it's readable.
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

