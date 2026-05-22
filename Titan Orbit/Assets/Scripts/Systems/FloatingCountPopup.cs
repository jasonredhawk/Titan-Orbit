using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Runtime world-space Canvas popup (same approach as <see cref="UI.PlanetStatsDisplay"/>).
    /// Rises on the XZ play plane, fades in/out, faces upward for the top-down camera.
    /// </summary>
    public class FloatingCountPopup : MonoBehaviour
    {
        private const float MinPopupWorldY = 4f;
        private static readonly Quaternion TopDownTextRotation = Quaternion.Euler(90f, 0f, 0f);

        private TextMeshProUGUI label;
        private Image iconImage;
        private Canvas canvas;
        private RectTransform rootRect;

        private Color baseColor = Color.white;
        private float elapsed;
        private float lifetime;
        private float riseSpeed;
        private float lockedY;
        private Vector3 lateralVelocity;

        public void Initialize(
            string message,
            Sprite iconSprite,
            Color color,
            TMP_FontAsset font,
            float fontSize,
            float duration,
            float riseSpeed,
            float iconScale,
            Vector3 iconLocalOffset,
            float lateralDriftSpeedMax = 0f
        )
        {
            if (UnityEngine.Camera.main == null)
            {
                Destroy(gameObject);
                return;
            }

            EnsureUi();

            if (label == null)
            {
                Debug.LogWarning("FloatingCountPopup: label missing; cannot initialize popup text.");
                Destroy(gameObject);
                return;
            }

            lifetime = Mathf.Max(0.1f, duration);
            this.riseSpeed = Mathf.Max(0.15f, riseSpeed);
            lateralVelocity = Vector3.zero;
            if (lateralDriftSpeedMax > 0.0001f)
            {
                var cam = UnityEngine.Camera.main;
                Vector3 rise = GetRiseDirectionOnPlayPlane(cam);
                Vector3 lateral = Vector3.Cross(Vector3.up, rise);
                if (lateral.sqrMagnitude < 1e-8f)
                    lateral = Vector3.right;
                lateral.Normalize();
                float mag = Random.Range(lateralDriftSpeedMax * 0.35f, lateralDriftSpeedMax);
                lateralVelocity = lateral * mag * (Random.value < 0.5f ? -1f : 1f);
            }

            elapsed = 0f;
            lockedY = Mathf.Max(transform.position.y, MinPopupWorldY);
            Vector3 initPos = transform.position;
            initPos.y = lockedY;
            transform.position = initPos;
            transform.rotation = TopDownTextRotation;

            baseColor = color;
            baseColor.a = 0f;

            label.text = message;
            if (font != null)
                label.font = font;
            label.fontSize = Mathf.Max(28f, fontSize * 8f);
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.richText = false;
            label.color = baseColor;
            label.raycastTarget = false;
            ApplyOutline(label, Color.black, 0.28f);
            label.ForceMeshUpdate();

            if (iconSprite != null && iconImage != null)
            {
                iconImage.sprite = iconSprite;
                iconImage.color = baseColor;
                iconImage.raycastTarget = false;
                iconImage.enabled = true;
                float iconPixels = Mathf.Max(20f, 40f * (iconScale / 0.1f));
                var iconRect = iconImage.rectTransform;
                iconRect.sizeDelta = new Vector2(iconPixels, iconPixels);
                iconRect.anchoredPosition = new Vector2(-72f + iconLocalOffset.x * 120f, iconLocalOffset.z * 120f);
            }
            else if (iconImage != null)
            {
                iconImage.enabled = false;
            }

            if (canvas != null)
            {
                canvas.worldCamera = UnityEngine.Camera.main;
            }
        }

        private void EnsureUi()
        {
            if (rootRect != null)
                return;

            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = UnityEngine.Camera.main;

            rootRect = gameObject.AddComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(280f, 72f);
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.localScale = new Vector3(0.0035f, 0.0035f, 0.0035f);

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.sizeDelta = new Vector2(260f, 64f);
            labelRect.anchoredPosition = Vector2.zero;

            label = labelGo.AddComponent<TextMeshProUGUI>();

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(transform, false);
            var iconRect = iconGo.AddComponent<RectTransform>();
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(1f, 0.5f);
            iconRect.anchoredPosition = new Vector2(-95f, 0f);
            iconImage = iconGo.AddComponent<Image>();
            iconImage.enabled = false;
        }

        private static void ApplyOutline(TextMeshProUGUI text, Color outlineColor, float outlineWidth)
        {
            if (text == null) return;
            Material mat = text.fontMaterial;
            if (mat == null) return;
            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor")) mat.SetColor("_OutlineColor", outlineColor);
            if (mat.HasProperty("_OutlineWidth")) mat.SetFloat("_OutlineWidth", Mathf.Clamp01(outlineWidth));
            if (mat.HasProperty("_OutlineSoftness")) mat.SetFloat("_OutlineSoftness", 0.05f);
        }

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

            if (canvas != null && canvas.worldCamera == null && UnityEngine.Camera.main != null)
                canvas.worldCamera = UnityEngine.Camera.main;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            var cam = UnityEngine.Camera.main;
            transform.position += GetRiseDirectionOnPlayPlane(cam) * riseSpeed * Time.deltaTime;
            if (lateralVelocity.sqrMagnitude > 0f)
                transform.position += lateralVelocity * Time.deltaTime;

            Vector3 pos = transform.position;
            pos.y = lockedY;
            transform.position = pos;

            float alpha = t <= 0.5f ? (t * 2f) : (1f - (t - 0.5f) * 2f);
            alpha = Mathf.Clamp01(alpha);

            Color c = baseColor;
            c.a = alpha;
            if (label != null)
                label.color = c;
            if (iconImage != null && iconImage.enabled)
                iconImage.color = c;

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }

        private void LateUpdate()
        {
            transform.rotation = TopDownTextRotation;

            Vector3 pos = transform.position;
            if (pos.y != lockedY)
            {
                pos.y = lockedY;
                transform.position = pos;
            }
        }
    }
}
