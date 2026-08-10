using UnityEngine;
using TMPro;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Generic floating world-space text: shows a string, rises upward, and fades in/out over a short duration.
    /// Supports both TextMeshPro (preferred) and legacy TextMesh.
    /// </summary>
    public class SimpleFloatingText : MonoBehaviour
    {
        [SerializeField] private float riseSpeed = 1.2f;

        [Header("Text References")]
        [Tooltip("Optional: assign a TextMeshPro component when using TMP.")]
        [SerializeField] private TMP_Text tmpText;
        [Tooltip("Optional: assign a TextMesh when using legacy 3D text.")]
        [SerializeField] private TextMesh textMesh;

        private float lifetime;
        private float elapsed;
        private Color baseColor;

        public void Initialize(string message, Color color, float duration)
        {
            // --- Initialize ---
            lifetime = Mathf.Max(0.1f, duration);

            // Prefer assigned TMP_Text, else find one in children.
            if (tmpText == null)
                tmpText = GetComponentInChildren<TMP_Text>();

            if (tmpText != null)
            {
                tmpText.text = message;
                baseColor = color;
                baseColor.a = 0f;
                tmpText.color = baseColor;
                tmpText.alignment = TextAlignmentOptions.Center;
                return;
            }

            // Fallback to legacy TextMesh if no TMP_Text is present.
            if (textMesh == null)
                textMesh = GetComponentInChildren<TextMesh>();

            if (textMesh == null)
            {
                Debug.LogWarning("SimpleFloatingText: No TMP_Text or TextMesh found on prefab.");
                return;
            }

            textMesh.text = message;
            baseColor = color;
            baseColor.a = 0f;
            textMesh.color = baseColor;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            // Reasonable default size; overall size still controlled by prefab scale.
            textMesh.fontSize = 24;
            textMesh.characterSize = 0.03f;
        }

        private void Update()
        {
            // --- Per-frame refresh ---
            if (lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            // Move up over time
            transform.position += Vector3.up * riseSpeed * Time.deltaTime;

            // Always face the main camera (billboard) so it's visible from top-down view.
            var cam = UnityEngine.Camera.main;
            if (cam != null)
            {
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
            }

            // Fade alpha for whichever text component is in use.
            float alpha = t <= 0.5f ? (t * 2f) : (1f - (t - 0.5f) * 2f);
            if (tmpText != null)
            {
                Color c = baseColor;
                c.a = Mathf.Clamp01(alpha);
                tmpText.color = c;
            }
            else if (textMesh != null)
            {
                // --- if ---
                Color c = baseColor;
                c.a = Mathf.Clamp01(alpha);
                textMesh.color = c;
            }

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }
    }
}

